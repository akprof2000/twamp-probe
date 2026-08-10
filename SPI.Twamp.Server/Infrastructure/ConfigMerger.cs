using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SPI.Twamp.Server.Infrastructure;

/// <summary>
/// Слияние конфигураций при обновлении.
/// </summary>
/// <remarks>
/// Задача простая на словах и неприятная на деле: в новой версии появляются
/// настройки, которых в файле администратора нет, — и он о них не узнает, пока
/// не полезет в документацию. А затирать его файл эталонным нельзя: там строки
/// подключения, адреса, ключ API и всё, ради чего сервер и настраивался.
/// <para>
/// Поэтому обновление делает слияние: значения администратора остаются как
/// есть, новые ключи из эталона добавляются рядом. Ничего не удаляется —
/// настройка, которой в новой версии больше нет, безвредна и может пригодиться
/// при откате.
/// </para>
/// <para>
/// Выполняет это сам сервер, а не установщик: разбирать JSON в shell-скрипте
/// нечем — jq на минимальной системе может не оказаться, а исполняемый файл
/// сервера лежит рядом всегда.
/// </para>
/// </remarks>
public static class ConfigMerger
{
    /// <summary>Аргумент командной строки, включающий режим слияния.</summary>
    public const string CommandLineSwitch = "--merge-config";

    /// <summary>
    /// Добавляет в текущий конфиг ключи, появившиеся в эталонном.
    /// Возвращает пути добавленного — установщику есть что показать.
    /// Текущего файла может не быть: это первая установка, тогда он просто
    /// копируется из эталона.
    /// </summary>
    public static IReadOnlyList<string> Merge(string currentPath, string referencePath)
    {
        JObject reference = ReadObject(referencePath);

        if (!File.Exists(currentPath))
        {
            Write(currentPath, reference);
            return [];
        }

        JObject current = ReadObject(currentPath);
        List<string> added = [];
        MergeInto(current, reference, prefix: string.Empty, added);

        if (added.Count == 0)
        {
            return []; // всё на месте — файл не трогаем
        }

        added.Sort(StringComparer.Ordinal);
        Write(currentPath, current);
        return added;
    }

    /// <summary>
    /// Дополняет <paramref name="current"/> ключами из <paramref name="reference"/>,
    /// не меняя существующие. Путь ключа копится в <paramref name="prefix"/>.
    /// </summary>
    private static void MergeInto(JObject current, JObject reference, string prefix, List<string> added)
    {
        foreach (JProperty property in reference.Properties())
        {
            string path = prefix.Length == 0 ? property.Name : $"{prefix}:{property.Name}";

            if (current[property.Name] is not JToken existing)
            {
                current[property.Name] = property.Value.DeepClone();
                added.Add(path);
                continue;
            }

            // Вложенные секции сливаем дальше: в секции могла появиться одна
            // новая настройка, и заменять её целиком нельзя.
            if (property.Value is JObject referenceSection && existing is JObject currentSection)
            {
                MergeInto(currentSection, referenceSection, path, added);
            }

            // Во всех прочих случаях значение администратора остаётся
            // нетронутым — в том числе если тип разошёлся: его выбор важнее.
        }
    }

    private static JObject ReadObject(string path)
    {
        // В файле, правленном под Windows, может оказаться UTF-8 BOM —
        // StreamReader снимает его сам, а вот разбор JSON им бы подавился.
        using StreamReader reader = new(path, detectEncodingFromByteOrderMarks: true);
        using JsonTextReader json = new(reader);
        return JObject.Load(json);
    }

    /// <summary>
    /// Записывает объект с отступами — файл читают и правят руками.
    /// Пишем через временный файл рядом: обрыв на середине записи оставил бы
    /// сервер вовсе без настроек, а так старый файл заменяется целиком и разом.
    /// </summary>
    private static void Write(string path, JObject document)
    {
        string temp = path + ".new";
        File.WriteAllText(temp, document.ToString(Formatting.Indented) + System.Environment.NewLine);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// Точка входа режима слияния: <c>SPI.Twamp.Server --merge-config текущий эталон</c>.
    /// Возвращает код возврата процесса.
    /// </summary>
    public static int RunCommandLine(string[] args)
    {
        if (args.Length != 3)
        {
            Console.Error.WriteLine(
                "использование: SPI.Twamp.Server --merge-config <текущий.json> <эталонный.json>");
            return 2;
        }

        try
        {
            foreach (string key in Merge(args[1], args[2]))
            {
                Console.WriteLine(key);
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"слияние настроек не удалось: {ex.Message}");
            return 1;
        }
    }
}
