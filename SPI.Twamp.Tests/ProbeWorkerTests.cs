// Ignore Spelling: SPI Twamp

using NLog;
using SPI.Twamp.Probe.Abstractions;
using SPI.Twamp.Probe.Contracts;
using SPI.Twamp.Probe.Server;
using Xunit;

namespace SPI.Twamp.Tests
{
    /// <summary>
    /// Тесты слияния задач в реестре пробы (Worker.MergeJobs).
    /// </summary>
    public class ProbeWorkerTests
    {
        /// <summary>Фейковый диспетчер: запоминает поставленные в очередь задачи.</summary>
        private sealed class FakeDispatcher : IProbeDispatcher
        {
            public List<TaskInfo> Enqueued { get; } = [];
            public void Enqueue(TaskInfo task) => Enqueued.Add(task);
        }

        /// <summary>Фейковое хранилище результатов (в тестах не используется).</summary>
        private sealed class FakeResultStore : IResultStore
        {
            public void Add(ActionData result) { }
            public Task<ResultBatch> TakeBatchAsync(TimeSpan timeout, CancellationToken ct) =>
                Task.FromResult(new ResultBatch());
            public Task<bool> ConfirmAsync(Guid batchId) => Task.FromResult(false);
            public Task LoadAsync(CancellationToken ct) => Task.CompletedTask;
        }

        private static TaskInfo Scheduler(Guid id) => new()
        {
            Id = id,
            Type = TaskType.Scheduler,
            CronExpression = "*/1 * * * *",
            End = DateTime.Now.AddDays(1)
        };

        /// <summary>Создаёт Worker с фейками и реальным реестром статусов.</summary>
        private static Worker CreateWorker(FakeDispatcher? dispatcher = null) =>
            new(LogManager.GetLogger("test"), dispatcher ?? new FakeDispatcher(),
                new FakeResultStore(), new TaskRunRegistry());

        [Fact(DisplayName = "Новая задача по расписанию попадает в реестр")]
        public async Task Merge_AddsScheduler()
        {
            using Worker worker = CreateWorker();
            Guid id = Guid.NewGuid();

            await worker.MergeJobs([Scheduler(id)], CancellationToken.None);

            Assert.Contains(id, worker.GetKnownTaskIds());
        }

        [Fact(DisplayName = "Задача с Delete=true удаляется из реестра")]
        public async Task Merge_RemovesDeleted()
        {
            using Worker worker = CreateWorker();
            Guid id = Guid.NewGuid();
            await worker.MergeJobs([Scheduler(id)], CancellationToken.None);

            TaskInfo stub = new() { Id = id, Type = TaskType.Scheduler, Delete = true };
            await worker.MergeJobs([stub], CancellationToken.None);

            Assert.DoesNotContain(id, worker.GetKnownTaskIds());
        }

        [Fact(DisplayName = "Разовая задача уходит в диспетчер и в реестре не хранится")]
        public async Task Merge_RepeaterGoesToDispatcher()
        {
            FakeDispatcher dispatcher = new();
            using Worker worker = CreateWorker(dispatcher);
            TaskInfo repeater = new() { Id = Guid.NewGuid(), Type = TaskType.Repeater };

            await worker.MergeJobs([repeater], CancellationToken.None);

            Assert.Single(dispatcher.Enqueued);
            Assert.Empty(worker.GetKnownTaskIds());
        }

        [Fact(DisplayName = "Повторное добавление той же задачи не создаёт дубликат")]
        public async Task Merge_UpdateIsIdempotent()
        {
            using Worker worker = CreateWorker();
            Guid id = Guid.NewGuid();

            await worker.MergeJobs([Scheduler(id)], CancellationToken.None);
            await worker.MergeJobs([Scheduler(id)], CancellationToken.None);

            Assert.Single(worker.GetKnownTaskIds(), id);
        }
    }

    /// <summary>
    /// Тесты составного статуса выполнения задач (TaskRunRegistry).
    /// </summary>
    public class TaskRunRegistryTests
    {
        [Fact(DisplayName = "Жизненный цикл: старт → выполняется, успех → счётчик и результат")]
        public void Lifecycle_Success()
        {
            TaskRunRegistry registry = new();
            TaskInfo task = new() { Id = Guid.NewGuid(), Title = "t1" };

            registry.MarkStarted(task);
            TaskRunInfo running = Assert.Single(registry.GetAll());
            Assert.Equal(RunOutcome.Running, running.LastOutcome);
            Assert.Equal(1, running.Running);

            registry.ReportOutcome(task.Id, RunOutcome.Success, 0, "итоговая строка");
            registry.MarkFinished(task.Id);

            TaskRunInfo done = Assert.Single(registry.GetAll());
            Assert.Equal(RunOutcome.Success, done.LastOutcome);
            Assert.Equal(0, done.LastExitCode);
            Assert.Equal("итоговая строка", done.LastResult);
            Assert.Equal(1, done.SuccessTotal);
            Assert.Equal(0, done.ErrorTotal);
            Assert.Null(done.LastError);
        }

        [Fact(DisplayName = "Ненулевой код выхода — ошибка со счётчиком и текстом")]
        public void Lifecycle_ExitCodeError()
        {
            TaskRunRegistry registry = new();
            TaskInfo task = new() { Id = Guid.NewGuid(), Title = "t2" };

            registry.MarkStarted(task);
            registry.ReportOutcome(task.Id, RunOutcome.ExitCodeError, 1, "Процесс зонда завершился с кодом 1.");
            registry.MarkFinished(task.Id);

            TaskRunInfo info = Assert.Single(registry.GetAll());
            Assert.Equal(RunOutcome.ExitCodeError, info.LastOutcome);
            Assert.Equal(1, info.LastExitCode);
            Assert.Equal(1, info.ErrorTotal);
            Assert.NotNull(info.LastError);
        }

        [Fact(DisplayName = "Успех после ошибки снимает залипшую ошибку")]
        public void Success_ClearsLastError()
        {
            TaskRunRegistry registry = new();
            Guid id = Guid.NewGuid();

            registry.ReportOutcome(id, RunOutcome.StartFailed, null, "не найден TWping");
            registry.ReportOutcome(id, RunOutcome.Success, 0, "ок");

            TaskRunInfo info = Assert.Single(registry.GetAll());
            Assert.Null(info.LastError);
            Assert.Equal(1, info.SuccessTotal);
            Assert.Equal(1, info.ErrorTotal);
        }
    }
}
