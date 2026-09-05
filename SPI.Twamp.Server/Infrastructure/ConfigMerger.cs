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
/// Файл правят руками, поэтому бережём и его вид: порядок ключей сохраняется,
/// строки не переосмысливаются (Json.NET иначе счёл бы «2025-01-01T10:00:00+05:00»
/// датой и переписал в локальном времени), права на файл остаются прежними.
/// Единственное, чего Json.NET сохранить не умеет, — комментарии, а .NET их в
/// appsettings.json допускает. Такой файл не переписываем вовсе: перечисляем
/// недостающие ключи, чтобы администратор добавил их сам.
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
    /// Код возврата: новые ключи есть, но файл не переписан — в нём комментарии.
    /// Установщик по нему меняет подпись к списку: «добавьте вручную».
    /// </summary>
    public const int ExitNotWritten = 3;

    /// <summary>Итог слияния.</summary>
    /// <param name="Added">Пути новых ключей вида <c>Probe:MinParallel</c>, по алфавиту.</param>
    /// <param name="Written">Записаны ли они в файл. Ложь — в файле комментарии, добавлять руками.</param>
    public sealed record Result(IReadOnlyList<string> Added, bool Written)
    {
        /// <summary>Нечего добавлять — файл не тронут.</summary>
        public static readonly Result Nothing = new([], Written: true);
    }

    /// <summary>
    /// Добавляет в текущий конфиг ключи, появившиеся в эталонном.
    /// Текущего файла может не быть: это первая установка, тогда он просто
    /// копируется из эталона.
    /// </summary>
    public static Result Merge(string currentPath, string referencePath)
    {
        JObject reference = ReadObject(File.ReadAllText(referencePath));

        if (!File.Exists(currentPath))
        {
            Write(currentPath, reference);
            return Result.Nothing;
        }

        string currentText = File.ReadAllText(currentPath);
        JObject current = ReadObject(currentText);
        List<string> added = [];
        MergeInto(current, reference, prefix: string.Empty, added);

        if (added.Count == 0)
        {
            return Result.Nothing; // всё на месте — файл не трогаем
        }

        added.Sort(StringComparer.Ordinal);
        if (HasComments(currentText))
        {
            return new Result(added, Written: false);
        }

        Write(currentPath, current);
        return new Result(added, Written: true);
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

    /// <summary>
    /// Разбирает текст в объект. Строки остаются строками: по умолчанию Json.NET
    /// распознаёт в них даты и при записи переводит в локальное время.
    /// </summary>
    private static JObject ReadObject(string text)
    {
        using JsonTextReader json = new(new StringReader(text))
        {
            DateParseHandling = DateParseHandling.None
        };
        return JObject.Load(json);
    }

    /// <summary>
    /// Есть ли в тексте комментарии. JObject их не хранит, так что после записи
    /// они бы пропали — а .NET-конфигурация комментарии в appsettings.json разрешает.
    /// </summary>
    private static bool HasComments(string text)
    {
        using JsonTextReader json = new(new StringReader(text))
        {
            DateParseHandling = DateParseHandling.None
        };
        while (json.Read())
        {
            if (json.TokenType == JsonToken.Comment)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Записывает объект с отступами — файл читают и правят руками.
    /// Пишем через временный файл рядом: обрыв на середине записи оставил бы
    /// сервер вовсе без настроек, а так старый файл заменяется целиком и разом.
    /// Права прежнего файла переносятся на новый: администратор мог закрыть его
    /// от чужих глаз из-за ключа API и пароля ClickHouse.
    /// </summary>
    private static void Write(string path, JObject document)
    {
        string temp = path + ".new";
        File.WriteAllText(temp, document.ToString(Formatting.Indented) + System.Environment.NewLine);
        if (!OperatingSystem.IsWindows() && File.Exists(path))
        {
            File.SetUnixFileMode(temp, File.GetUnixFileMode(path));
        }
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// Точка входа режима слияния: <c>SPI.Twamp.Server --merge-config текущий эталон</c>.
    /// Печатает новые ключи по одному в строке; код возврата — 0, если они
    /// записаны, <see cref="ExitNotWritten"/>, если их нужно добавить руками.
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
            Result result = Merge(args[1], args[2]);
            foreach (string key in result.Added)
            {
                Console.WriteLine(key);
            }
            return result.Written ? 0 : ExitNotWritten;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"слияние настроек не удалось: {ex.Message}");
            return 1;
        }
    }
}
