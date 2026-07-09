// Ignore Spelling: SPI Twamp

using NLog;
using spi.twamp.server.Environment;
using SPI.Twamp.Server.Abstractions;
using SPI.Twamp.Server.Contracts;

namespace SPI.Twamp.Server.BackgroundServices
{
    /// <summary>
    /// Фоновая очистка БД (ретенция), чтобы база не росла бесконечно:
    /// <list type="bullet">
    /// <item>сырые результаты (actiondata) старше «Retention:RawDays» удаляются;</item>
    /// <item>разобранная статистика (stats) старше «Retention:StatsDays» удаляется;</item>
    /// <item>задачи, помеченные удалёнными более «Retention:DeletedTaskDays» назад,
    /// вычищаются окончательно (пробы к этому моменту уже синхронизированы,
    /// а осиротевшие копии на пробах уберёт фоновая сверка).</item>
    /// </list>
    /// </summary>
    public sealed class MaintenanceService(Logger logger, IConfiguration configuration,
        IActionRepository actions, IStatRepository stats, ITaskRepository tasks)
        : BackgroundService
    {
        private readonly Logger _logger = logger;
        private readonly IActionRepository _actions = actions;
        private readonly IStatRepository _stats = stats;
        private readonly ITaskRepository _tasks = tasks;

        /// <summary>Интервал между проходами очистки, минут.</summary>
        private readonly int _intervalMinutes = configuration["Retention:IntervalMin"].ConvertTo(60);

        /// <summary>Срок хранения сырых результатов, дней.</summary>
        private readonly int _rawDays = configuration["Retention:RawDays"].ConvertTo(14);

        /// <summary>Срок хранения разобранной статистики, дней.</summary>
        private readonly int _statsDays = configuration["Retention:StatsDays"].ConvertTo(90);

        /// <summary>Через сколько дней окончательно вычищать удалённые задачи.</summary>
        private readonly int _deletedTaskDays = configuration["Retention:DeletedTaskDays"].ConvertTo(7);

        /// <summary>Периодический цикл очистки.</summary>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.Info("Очистка БД: raw {Raw} дн., stats {Stats} дн., удалённые задачи {Del} дн., интервал {Int} мин.",
                _rawDays, _statsDays, _deletedTaskDays, _intervalMinutes);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RunOnceAsync();
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Ошибка фоновой очистки БД");
                }

                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(_intervalMinutes), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        /// <summary>Один проход очистки всех коллекций.</summary>
        private async Task RunOnceAsync()
        {
            DateTime now = DateTime.Now;

            int rawDeleted = await _actions.DeleteOlderAsync(now.AddDays(-_rawDays));
            int statsDeleted = await _stats.DeleteOlderAsync(now.AddDays(-_statsDays));

            // Окончательная очистка давно удалённых задач: их немного, поэтому
            // фильтруем в памяти (надёжнее, чем транслировать nullable-условие в БД).
            int tasksPurged = 0;
            DateTime taskCutoff = now.AddDays(-_deletedTaskDays);
            foreach (TaskInfo task in await _tasks.GetAllAsync())
            {
                if (!task.Delete)
                {
                    continue;
                }

                // Для старых записей без DeletedAt ориентируемся на дату окончания.
                DateTime reference = task.DeletedAt ?? task.End;
                if (reference < taskCutoff)
                {
                    await _tasks.RemoveAsync(task.Id);
                    tasksPurged++;
                }
            }

            if (rawDeleted > 0 || statsDeleted > 0 || tasksPurged > 0)
            {
                _logger.Info("Очистка БД: удалено raw {Raw}, stats {Stats}, задач {Tasks}",
                    rawDeleted, statsDeleted, tasksPurged);
            }
        }
    }
}
