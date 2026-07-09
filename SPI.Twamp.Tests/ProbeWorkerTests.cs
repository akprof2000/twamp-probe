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

        [Fact(DisplayName = "Новая задача по расписанию попадает в реестр")]
        public async Task Merge_AddsScheduler()
        {
            using Worker worker = new(LogManager.GetLogger("test"), new FakeDispatcher(), new FakeResultStore());
            Guid id = Guid.NewGuid();

            await worker.MergeJobs([Scheduler(id)], CancellationToken.None);

            Assert.Contains(id, worker.GetKnownTaskIds());
        }

        [Fact(DisplayName = "Задача с Delete=true удаляется из реестра")]
        public async Task Merge_RemovesDeleted()
        {
            using Worker worker = new(LogManager.GetLogger("test"), new FakeDispatcher(), new FakeResultStore());
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
            using Worker worker = new(LogManager.GetLogger("test"), dispatcher, new FakeResultStore());
            TaskInfo repeater = new() { Id = Guid.NewGuid(), Type = TaskType.Repeater };

            await worker.MergeJobs([repeater], CancellationToken.None);

            Assert.Single(dispatcher.Enqueued);
            Assert.Empty(worker.GetKnownTaskIds());
        }

        [Fact(DisplayName = "Повторное добавление той же задачи не создаёт дубликат")]
        public async Task Merge_UpdateIsIdempotent()
        {
            using Worker worker = new(LogManager.GetLogger("test"), new FakeDispatcher(), new FakeResultStore());
            Guid id = Guid.NewGuid();

            await worker.MergeJobs([Scheduler(id)], CancellationToken.None);
            await worker.MergeJobs([Scheduler(id)], CancellationToken.None);

            Assert.Single(worker.GetKnownTaskIds(), id);
        }
    }
}
