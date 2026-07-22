namespace SPI.Twamp.Server.Parser
{
    /// <summary>
    /// Разобранная статистика одного сеанса TWamp/ping.
    /// </summary>
    public class TwPingStats
    {
        /// <summary>
        /// Дата и время запуска задачи (момент выполнения замера пробой) —
        /// первая колонка отчёта.
        /// </summary>
        public DateTime? Started { get; set; }
        /// <summary>
        /// Идентификатор задачи, к которой относится статистика.
        /// </summary>
        public Guid? Id { get; set; }
        /// <summary>
        /// Название задачи.
        /// </summary>
        public string? Title { get; set; }
        /// <summary>
        /// Тип запроса, которым выполнена задача (WinPing / TWamp / TWampy).
        /// </summary>
        public string? Mode { get; set; }
        /// <summary>
        /// Фактическая строка вызова зонда (для идентификации ответа).
        /// </summary>
        public string? CallLine { get; set; }
        /// <summary>
        /// Хост-источник.
        /// </summary>
        public string? FromHost { get; set; }
        /// <summary>
        /// Порт источника.
        /// </summary>
        public int? FromPort { get; set; }
        /// <summary>
        /// Хост-получатель.
        /// </summary>
        public string? ToHost { get; set; }
        /// <summary>
        /// Порт получателя.
        /// </summary>
        public int? ToPort { get; set; }

        /// <summary>
        /// Идентификатор сеанса (SID).
        /// </summary>
        public string? Sid { get; set; }
        /// <summary>
        /// Время первого пакета.
        /// </summary>
        public DateTime? First { get; set; }
        /// <summary>
        /// Время последнего пакета.
        /// </summary>
        public DateTime? Last { get; set; }
        /// <summary>
        /// Отправлено пакетов.
        /// </summary>
        public int? Sent { get; set; }
        /// <summary>
        /// Потеряно пакетов.
        /// </summary>
        public int? Lost { get; set; }
        /// <summary>
        /// Процент потерь.
        /// </summary>
        public double? LossPercent { get; set; }

        /// <summary>
        /// Минимальный RTT (круговая задержка).
        /// </summary>
        public double? RttMin { get; set; }
        /// <summary>
        /// Медианный RTT.
        /// </summary>
        public double? RttMedian { get; set; }
        /// <summary>
        /// Максимальный RTT.
        /// </summary>
        public double? RttMax { get; set; }

        /// <summary>
        /// Минимальная задержка в прямом направлении (отправка).
        /// </summary>
        public double? SendMin { get; set; }
        /// <summary>
        /// Медианная задержка в прямом направлении.
        /// </summary>
        public double? SendMedian { get; set; }
        /// <summary>
        /// Максимальная задержка в прямом направлении.
        /// </summary>
        public double? SendMax { get; set; }

        /// <summary>
        /// Минимальная задержка в обратном направлении (отражение).
        /// </summary>
        public double? ReflectMin { get; set; }
        /// <summary>
        /// Медианная задержка в обратном направлении.
        /// </summary>
        public double? ReflectMedian { get; set; }
        /// <summary>
        /// Максимальная задержка в обратном направлении.
        /// </summary>
        public double? ReflectMax { get; set; }

        /// <summary>
        /// Минимальное время обработки на отражателе.
        /// </summary>
        public double? ReflectProcMin { get; set; }
        /// <summary>
        /// Максимальное время обработки на отражателе.
        /// </summary>
        public double? ReflectProcMax { get; set; }

        /// <summary>
        /// Двусторонний джиттер.
        /// </summary>
        public double? TwoWayJitter { get; set; }
        /// <summary>
        /// Джиттер в прямом направлении.
        /// </summary>
        public double? SendJitter { get; set; }
        /// <summary>
        /// Джиттер в обратном направлении.
        /// </summary>
        public double? ReflectJitter { get; set; }

        /// <summary>
        /// Количество переходов (hops) в прямом направлении.
        /// </summary>
        public int? SendHops { get; set; }
        /// <summary>
        /// Количество переходов (hops) в обратном направлении.
        /// </summary>
        public int? ReflectHops { get; set; }
        /// <summary>
        /// Текст ошибок, если разбор не удался.
        /// </summary>
        public string? Errors { get; set; }
    }
}
