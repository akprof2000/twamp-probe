// Ignore Spelling: SPI Twamp

using SPI.Twamp.Server.Abstractions;

namespace SPI.Twamp.Server.Infrastructure
{
    /// <summary>
    /// Реализация шины изменений: счётчик версии + широковещательный
    /// <see cref="TaskCompletionSource"/>, пересоздаваемый на каждом уведомлении.
    /// Все ожидающие клиенты пробуждаются одним сигналом; ожидание не держит поток.
    /// </summary>
    public sealed class ChangeNotifier : IChangeNotifier
    {
        private readonly object _lock = new();
        private long _version;
        private TaskCompletionSource _signal = NewSignal();

        /// <summary>Создаёт сигнал с асинхронным пробуждением ожидающих (без инлайнинга продолжений).</summary>
        private static TaskCompletionSource NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <inheritdoc/>
        public long Version
        {
            get { lock (_lock) { return _version; } }
        }

        /// <inheritdoc/>
        public void Notify()
        {
            lock (_lock)
            {
                _version++;
                _signal.TrySetResult();   // будим всех ожидающих
                _signal = NewSignal();    // следующие ожидающие подпишутся на новый сигнал
            }
        }

        /// <inheritdoc/>
        public async Task<long> WaitAsync(long knownVersion, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Task waitTask;
            lock (_lock)
            {
                if (_version > knownVersion)
                {
                    return _version; // изменение уже произошло — отвечаем сразу
                }
                waitTask = _signal.Task;
            }

            // Ждём сигнала либо таймаута; отмена (клиент отключился) прерывает ожидание.
            _ = await Task.WhenAny(waitTask, Task.Delay(timeout, cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();

            return Version;
        }
    }
}
