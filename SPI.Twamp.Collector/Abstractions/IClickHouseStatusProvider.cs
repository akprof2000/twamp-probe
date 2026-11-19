// Ignore Spelling: SPI Twamp Clickhouse

using SPI.Twamp.Server.Contracts;

namespace SPI.Twamp.Server.Abstractions
{
    /// <summary>
    /// Поставщик состояния переноса результатов в ClickHouse для веб-интерфейса.
    /// </summary>
    public interface IClickHouseStatusProvider
    {
        /// <summary>Текущее состояние выгрузки: объёмы, очередь и связь с базой.</summary>
        ClickHouseState GetState();
    }
}
