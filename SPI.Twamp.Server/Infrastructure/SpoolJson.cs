// Ignore Spelling: SPI Twamp Clickhouse ndjson

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SPI.Twamp.Server.Infrastructure
{
    /// <summary>
    /// Настройки сериализации строк буфера. Формат файла сегмента — NDJSON
    /// (по одному JSON-объекту в строке), он же формат <c>JSONEachRow</c> ClickHouse:
    /// поэтому файл сегмента отправляется в базу потоком как есть, без разбора.
    /// </summary>
    public static class SpoolJson
    {
        /// <summary>
        /// Имена свойств — как есть (совпадают с именами колонок таблицы), <c>null</c>
        /// сохраняются (ClickHouse кладёт их в Nullable-колонки), кириллица не экранируется.
        /// </summary>
        public static readonly JsonSerializerOptions Options = new()
        {
            PropertyNamingPolicy = null,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = false
        };
    }
}
