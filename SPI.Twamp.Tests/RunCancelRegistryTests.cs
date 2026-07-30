// Ignore Spelling: SPI Twamp

using SPI.Twamp.Probe.Server;
using Xunit;

namespace SPI.Twamp.Tests
{
    /// <summary>
    /// Тесты реестра активных запусков: через него удаление задачи обрывает
    /// уже работающий зонд, а не ждёт его естественного завершения.
    /// </summary>
    public class RunCancelRegistryTests
    {
        [Fact(DisplayName = "Отмена задачи прерывает её активные запуски")]
        public void CancelTask_CancelsTrackedRuns()
        {
            RunCancelRegistry registry = new();
            Guid taskId = Guid.NewGuid();

            using CancellationTokenSource first = new();
            using CancellationTokenSource second = new();
            using IDisposable t1 = registry.Track(taskId, first);
            using IDisposable t2 = registry.Track(taskId, second);

            Assert.Equal(2, registry.ActiveRuns(taskId));
            Assert.Equal(2, registry.CancelTask(taskId));

            Assert.True(first.IsCancellationRequested, "первый запуск не оборван");
            Assert.True(second.IsCancellationRequested, "второй запуск не оборван");
            Assert.Equal(0, registry.ActiveRuns(taskId));
        }

        [Fact(DisplayName = "Отмена задачи не трогает запуски других задач")]
        public void CancelTask_LeavesOtherTasksRunning()
        {
            RunCancelRegistry registry = new();
            Guid deleted = Guid.NewGuid();
            Guid alive = Guid.NewGuid();

            using CancellationTokenSource deletedRun = new();
            using CancellationTokenSource aliveRun = new();
            using IDisposable t1 = registry.Track(deleted, deletedRun);
            using IDisposable t2 = registry.Track(alive, aliveRun);

            _ = registry.CancelTask(deleted);

            Assert.True(deletedRun.IsCancellationRequested);
            Assert.False(aliveRun.IsCancellationRequested, "задел чужой запуск");
            Assert.Equal(1, registry.ActiveRuns(alive));
        }

        [Fact(DisplayName = "Завершённый запуск снимается с учёта")]
        public void FinishedRun_IsUntracked()
        {
            RunCancelRegistry registry = new();
            Guid taskId = Guid.NewGuid();

            using (CancellationTokenSource cts = new())
            using (registry.Track(taskId, cts))
            {
                Assert.Equal(1, registry.ActiveRuns(taskId));
            }

            // Иначе реестр рос бы бесконечно, а отмена трогала бы давно завершённые запуски.
            Assert.Equal(0, registry.ActiveRuns(taskId));
            Assert.Equal(0, registry.CancelTask(taskId));
        }

        [Fact(DisplayName = "CancelAll обрывает запуски всех задач")]
        public void CancelAll_CancelsEverything()
        {
            RunCancelRegistry registry = new();
            using CancellationTokenSource a = new();
            using CancellationTokenSource b = new();
            using IDisposable t1 = registry.Track(Guid.NewGuid(), a);
            using IDisposable t2 = registry.Track(Guid.NewGuid(), b);

            Assert.Equal(2, registry.CancelAll());
            Assert.True(a.IsCancellationRequested);
            Assert.True(b.IsCancellationRequested);
        }

        [Fact(DisplayName = "Отмена уже освобождённого запуска не роняет реестр")]
        public void CancelTask_SurvivesDisposedRun()
        {
            // Гонка: запуск завершился и освободил источник ровно в момент удаления задачи.
            RunCancelRegistry registry = new();
            Guid taskId = Guid.NewGuid();
            CancellationTokenSource cts = new();
            _ = registry.Track(taskId, cts);
            cts.Dispose();

            int stopped = registry.CancelTask(taskId);

            Assert.Equal(0, stopped); // обрывать было нечего, но исключения быть не должно
        }
    }
}
