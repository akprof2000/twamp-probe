// Ignore Spelling: SPI Twamp

using SPI.Twamp.Server.Infrastructure;
using Xunit;

namespace SPI.Twamp.Tests
{
    /// <summary>
    /// Тесты слияния настроек при обновлении: значения администратора остаются,
    /// новые ключи добавляются, файл без нужды не переписывается, а то, что
    /// сохранить нельзя (комментарии), не теряется молча.
    /// </summary>
    public class ConfigMergerTests : IDisposable
    {
        private readonly string _directory =
            Path.Combine(Path.GetTempPath(), "twamp-config-" + Guid.NewGuid().ToString("N"));

        public ConfigMergerTests() => Directory.CreateDirectory(_directory);

        public void Dispose()
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }

        /// <summary>Кладёт файл настроек во временный каталог теста.</summary>
        private string WriteConfig(string name, string content)
        {
            string path = Path.Combine(_directory, name);
            File.WriteAllText(path, content);
            return path;
        }

        [Fact]
        public void Merge_KeepsAdminValuesAndAddsNewKeysInPlace()
        {
            // Главное свойство обновления: настройки администратора остаются как
            // есть, появившиеся в новой версии добавляются рядом, а порядок
            // ключей — чья-то ручная работа — не перемешивается.
            string current = WriteConfig("appsettings.json", """
                {
                  "Urls": "http://0.0.0.0:9443",
                  "Probe": { "HttpTimeoutSec": 5, "CleanupWaitHours": 48 },
                  "Auth": { "ApiKey": "секрет-администратора" }
                }
                """);
            string reference = WriteConfig("reference.json", """
                {
                  "Auth": { "ApiKey": "" },
                  "Retention": { "RawDays": 14 },
                  "Probe": { "HttpTimeoutSec": 60, "ReconcileIntervalSec": 30 },
                  "Urls": "http://0.0.0.0:9000"
                }
                """);

            ConfigMerger.Result result = ConfigMerger.Merge(current, reference);

            Assert.True(result.Written);
            Assert.Equal(["Probe:ReconcileIntervalSec", "Retention"], result.Added);

            string text = File.ReadAllText(current);
            Assert.Contains("\"Urls\": \"http://0.0.0.0:9443\"", text);
            Assert.Contains("\"HttpTimeoutSec\": 5,", text);
            Assert.Contains("\"ApiKey\": \"секрет-администратора\"", text);
            Assert.Contains("\"ReconcileIntervalSec\": 30", text);
            Assert.Contains("\"RawDays\": 14", text);

            // Порядок прежний: Urls, Probe (со своими ключами, новый — последним), Auth, затем новое.
            string[] order = ["\"Urls\"", "\"Probe\"", "\"HttpTimeoutSec\"", "\"CleanupWaitHours\"",
                "\"ReconcileIntervalSec\"", "\"Auth\"", "\"Retention\""];
            int last = -1;
            foreach (string key in order)
            {
                int at = text.IndexOf(key, StringComparison.Ordinal);
                Assert.True(at > last, $"{key} не на своём месте:\n{text}");
                last = at;
            }
        }

        [Fact]
        public void Merge_FirstInstallCopiesReference()
        {
            // Первая установка: своего файла ещё нет, берётся эталон целиком.
            string reference = WriteConfig("reference.json", """{ "Urls": "http://0.0.0.0:9000" }""");
            string current = Path.Combine(_directory, "appsettings.json");

            ConfigMerger.Result result = ConfigMerger.Merge(current, reference);

            Assert.Empty(result.Added);
            Assert.Contains("\"Urls\": \"http://0.0.0.0:9000\"", File.ReadAllText(current));
        }

        [Fact]
        public void Merge_NothingNewLeavesFileUntouched()
        {
            // Обновление без новых настроек не должно трогать файл вовсе: ни
            // переформатировать, ни менять время изменения.
            const string original = "{\n\t\"Urls\": \"http://0.0.0.0:9443\"\n}";
            string current = WriteConfig("appsettings.json", original);
            string reference = WriteConfig("reference.json", """{ "Urls": "http://0.0.0.0:9000" }""");
            DateTime before = File.GetLastWriteTimeUtc(current);

            ConfigMerger.Result result = ConfigMerger.Merge(current, reference);

            Assert.Empty(result.Added);
            Assert.Equal(original, File.ReadAllText(current));
            Assert.Equal(before, File.GetLastWriteTimeUtc(current));
        }

        [Fact]
        public void Merge_BadJsonKeepsFile()
        {
            // Испорченный файл лучше не трогать: слить его нельзя, а перезаписать
            // эталоном — потерять настройки. Установщик покажет ошибку и остановится.
            const string broken = """{ "Urls": "http://0.0.0.0:9443", """;
            string current = WriteConfig("appsettings.json", broken);
            string reference = WriteConfig("reference.json", """{ "Urls": "http://0.0.0.0:9000" }""");

            Assert.ThrowsAny<Exception>(() => ConfigMerger.Merge(current, reference));
            Assert.Equal(broken, File.ReadAllText(current));
        }

        [Fact]
        public void Merge_FileWithCommentsIsNotRewritten()
        {
            // .NET допускает комментарии в appsettings.json, а Json.NET сохранить
            // их не умеет. Такой файл не переписываем: перечисляем недостающее и
            // сообщаем, что добавлять придётся руками.
            string original = """
                {
                  // ключ выдан по заявке 4711
                  "Auth": { "ApiKey": "секрет" },
                  "Urls": "http://0.0.0.0:9443"
                }
                """;
            string current = WriteConfig("appsettings.json", original);
            string reference = WriteConfig("reference.json", """
                { "Auth": { "ApiKey": "" }, "Urls": "http://0.0.0.0:9000", "Retention": { "RawDays": 14 } }
                """);

            ConfigMerger.Result result = ConfigMerger.Merge(current, reference);

            Assert.False(result.Written);
            Assert.Equal(["Retention"], result.Added);
            Assert.Equal(original, File.ReadAllText(current));
        }

        [Fact]
        public void Merge_KeepsDateLikeStringsVerbatim()
        {
            // Json.NET по умолчанию распознаёт в строках даты и при записи
            // переводит их в локальное время машины. Настройка администратора
            // должна остаться тем же текстом.
            string current = WriteConfig("appsettings.json", """
                { "Since": "2025-01-01T10:00:00+05:00", "Day": "2025-01-01" }
                """);
            string reference = WriteConfig("reference.json", """
                { "Since": "", "Day": "", "New": 1 }
                """);

            ConfigMerger.Merge(current, reference);

            string text = File.ReadAllText(current);
            Assert.Contains("\"Since\": \"2025-01-01T10:00:00+05:00\"", text);
            Assert.Contains("\"Day\": \"2025-01-01\"", text);
        }
    }
}
