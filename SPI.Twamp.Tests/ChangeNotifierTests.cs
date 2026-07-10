// Ignore Spelling: SPI Twamp

using SPI.Twamp.Server.Infrastructure;
using Xunit;

namespace SPI.Twamp.Tests
{
    /// <summary>
    /// Тесты шины изменений для «длинного опроса» веб-интерфейса.
    /// </summary>
    public class ChangeNotifierTests
    {
        [Fact(DisplayName = "Известная старая версия — ответ немедленно, без ожидания")]
        public async Task Wait_ReturnsImmediately_WhenVersionIsStale()
        {
            ChangeNotifier notifier = new();
            notifier.Notify(); // версия стала 1

            long version = await notifier.WaitAsync(0, TimeSpan.FromSeconds(30), CancellationToken.None);

            Assert.Equal(1, version);
        }

        [Fact(DisplayName = "Notify будит ожидающего клиента до истечения таймаута")]
        public async Task Wait_WakesUp_OnNotify()
        {
            ChangeNotifier notifier = new();
            Task<long> waiting = notifier.WaitAsync(0, TimeSpan.FromSeconds(30), CancellationToken.None);
            Assert.False(waiting.IsCompleted);

            notifier.Notify();

            long version = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, version);
        }

        [Fact(DisplayName = "Без изменений ожидание завершается по таймауту с той же версией")]
        public async Task Wait_TimesOut_WithoutChanges()
        {
            ChangeNotifier notifier = new();

            long version = await notifier.WaitAsync(0, TimeSpan.FromMilliseconds(100), CancellationToken.None);

            Assert.Equal(0, version);
        }

        [Fact(DisplayName = "Один Notify будит всех ожидающих")]
        public async Task Notify_WakesAllWaiters()
        {
            ChangeNotifier notifier = new();
            Task<long> first = notifier.WaitAsync(0, TimeSpan.FromSeconds(30), CancellationToken.None);
            Task<long> second = notifier.WaitAsync(0, TimeSpan.FromSeconds(30), CancellationToken.None);

            notifier.Notify();

            long[] versions = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.All(versions, v => Assert.Equal(1, v));
        }
    }
}
