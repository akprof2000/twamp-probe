// Ignore Spelling: SPI Twamp

using NLog;
using spi.twamp.server.Environment;
using SPI.Twamp.Server.Abstractions;
using SPI.Twamp.Server.Contracts;
using SPI.Twamp.Server.Infrastructure;
using System.Diagnostics;

namespace SPI.Twamp.Server.BackgroundServices
{
    /// <summary>
    /// Фоновое обслуживание БД:
    /// <list type="bullet">
    /// <item>частый checkpoint LiteDB (каждые «Database:CheckpointMin» минут) — переносит
    /// WAL-журнал («*-log.db») в основную базу, иначе журнал растёт бесконечно;</item>
    /// <item>ретенция: сырые результаты старше «Retention:RawDays», статистика старше
    /// «Retention:StatsDays», задачи, удалённые более «Retention:DeletedTaskDays» назад.</item>
    /// </list>
    /// </summary>
    public sealed class MaintenanceService(Logger logger, IConfiguration configuration,
        LiteDbContext dbContext, IActionRepository actions, IStatRepository stats, ITaskRepository tasks)
        : BackgroundService
    {
        private readonly Logger _logger = logger;
        private readonly LiteDbContext _dbContext = dbContext;
        private readonly IActionRepository _actions = actions;
        private readonly IStatRepository _stats = stats;
        private readonly ITaskRepository _tasks = tasks;

        /// <summary>Интервал переноса WAL-журнала в основную базу, минут.</summary>
        private readonly int _checkpointMinutes = configuration["Database:CheckpointMin"].ConvertTo(5);

        /// <summary>Интервал между проходами очистки, минут.</summary>
        private readonly int _intervalMinutes = configuration["Retention:IntervalMin"].ConvertTo(60);

        /// <summary>Срок хранения сырых результатов, дней.</summary>
        private readonly int _rawDays = configuration["Retention:RawDays"].ConvertTo(14);

        /// <summary>Срок хранения разобранной статистики, дней.</summary>
        private readonly int _statsDays = configuration["Retention:StatsDays"].ConvertTo(90);

        /// <summary>Через сколько дней окончательно вычищать удалённые задачи.</summary>
        private readonly int _deletedTaskDays = configuration["Retention:DeletedTaskDays"].ConvertTo(7);

        /// <summary>Периодический цикл: checkpoint — часто, полная очистка — редко.</summary>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.Info(
                "Обслуживание БД: checkpoint каждые {Chk} мин.; ретенция raw {Raw} дн., stats {Stats} дн., удалённые задачи {Del} дн., каждые {Int} мин.",
                _checkpointMinutes, _rawDays, _statsDays, _deletedTaskDays, _intervalMinutes);

            // Монотонный таймер: не зависит от перевода системных часов и DST.
            Stopwatch sinceCleanup = Stopwatch.StartNew();
            bool firstPass = true;

            while (!stoppingToken.IsCancellationRequested)
            {
                // Редкая полная очистка по своему интервалу (первый проход — сразу).
                try
                {
                    if (firstPass || sinceCleanup.Elapsed.TotalMinutes >= _intervalMinutes)
                    {
                        await RunOnceAsync();
                        sinceCleanup.Restart();
                        firstPass = false;
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Ошибка ретенции БД");
                }

                await TryCheckpointAsync();

                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(_checkpointMinutes), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        /// <summary>
        /// Best-effort checkpoint WAL-журнала. Под нагрузкой LiteDB не всегда успевает взять
        /// эксклюзивную блокировку и бросает «lock timeout» — это НЕ ошибка обслуживания:
        /// размер журнала всё равно ограничивает встроенный авто-checkpoint LiteDB (по порогу
        /// в страницах, срабатывает на пути записи). Поэтому такой случай логируем кратким
        /// предупреждением, а не ошибкой со стеком.
        /// </summary>
        /// <summary>Сколько ближайших циклов пропустить checkpoint после провала по блокировке.</summary>
        private int _checkpointSkip;

        private async Task TryCheckpointAsync()
        {
            // После провала под нагрузкой не пытаемся каждые CheckpointMin минут (иначе
            // впустую блокируемся до таймаута) — даём БД передышку на несколько циклов.
            if (_checkpointSkip > 0)
            {
                _checkpointSkip--;
                return;
            }

            try
            {
                await _dbContext.CheckpointAsync();
                _logger.Debug("Checkpoint LiteDB выполнен");
            }
            catch (Exception ex) when (IsLockTimeout(ex))
            {
                _checkpointSkip = 6; // при CheckpointMin=5 — следующая попытка примерно через полчаса
                _logger.Warn("Checkpoint пропущен: БД занята (журнал сведёт авто-checkpoint LiteDB)");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Ошибка checkpoint БД");
            }
        }

        /// <summary>Проверяет, вызвано ли исключение таймаутом блокировки LiteDB.</summary>
        private static bool IsLockTimeout(Exception ex)
        {
            for (Exception? e = ex; e is not null; e = e.InnerException)
            {
                if (e.Message.Contains("lock timeout", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
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
