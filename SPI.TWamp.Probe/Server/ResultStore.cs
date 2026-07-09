// Ignore Spelling: SPI Twamp

using Newtonsoft.Json;
using NLog;
using spi.twamp.Probe.Environment;
using SPI.Twamp.Probe.Abstractions;
using SPI.Twamp.Probe.Contracts;
using System.Collections.Concurrent;
using System.Threading.Channels;

namespace SPI.Twamp.Probe.Server
{
    /// <summary>
    /// Реализация хранилища результатов с подтверждением доставки.
    /// <para>
    /// Результаты копятся в неблокирующей очереди; сервер забирает их пачкой
    /// (<see cref="TakeBatchAsync"/>), пачка переходит в состояние «в полёте» и
    /// удаляется только после подтверждения (<see cref="ConfirmAsync"/>). Если сервер
    /// упал между получением и записью в БД — при следующем опросе проба выдаст ту же
    /// пачку повторно, а дубликаты сервер отбросит по <see cref="ActionData.ResultId"/>.
    /// </para>
    /// <para>
    /// Очередь ограничена (настройка «Probe:MaxPendingResults»): при длительной
    /// недоступности сервера старые результаты вытесняются, не давая пробе
    /// исчерпать память и диск.
    /// </para>
    /// </summary>
    public sealed class ResultStore : IResultStore, IDisposable
    {
        /// <summary>Файл для сохранения ещё не доставленных результатов между перезапусками.</summary>
        private const string PersistenceFileName = "JobResult.json";

        /// <summary>Интервал фонового сохранения снимка очереди на диск.</summary>
        private static readonly TimeSpan PersistInterval = TimeSpan.FromSeconds(1);

        private readonly Logger _logger;

        /// <summary>Максимум результатов в очереди; сверх лимита вытесняются самые старые.</summary>
        private readonly int _maxPending;

        /// <summary>Очередь накопленных, ещё не выданных результатов.</summary>
        private readonly ConcurrentQueue<ActionData> _pending = new();
        private int _pendingCount;
        private long _droppedTotal;

        /// <summary>Выданная, но ещё не подтверждённая сервером пачка (максимум одна).</summary>
        private readonly object _batchLock = new();
        private Guid _inFlightId;
        private List<ActionData>? _inFlight;

        /// <summary>Сериализует выдачу пачек при параллельных опросах.</summary>
        private readonly SemaphoreSlim _takeLock = new(1, 1);

        /// <summary>
        /// Канал-сигнал ёмкостью 1 (аналог асинхронного AutoResetEvent): множество
        /// добавлений «схлопываются» в один сигнал для ожидающего потребителя.
        /// </summary>
        private readonly Channel<bool> _signal =
            Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropWrite
            });

        /// <summary>Сериализует запись файла, чтобы избежать одновременных обращений к диску.</summary>
        private readonly SemaphoreSlim _fileLock = new(1, 1);

        /// <summary>Таймер фонового сохранения снимка очереди на диск.</summary>
        private Timer? _persistTimer;

        /// <summary>Признак наличия несохранённых изменений (снижает лишние записи на диск).</summary>
        private volatile bool _dirty;
        private volatile bool _disposed;

        /// <summary>Создаёт хранилище и читает лимит очереди из конфигурации.</summary>
        public ResultStore(Logger logger, IConfiguration configuration)
        {
            _logger = logger;
            int limit = configuration["Probe:MaxPendingResults"].ConvertTo(100_000);
            _maxPending = limit > 0 ? limit : 100_000;
        }

        /// <inheritdoc/>
        public void Add(ActionData result)
        {
            _pending.Enqueue(result);

            // Ограничение очереди: при переполнении вытесняем самый старый результат.
            if (Interlocked.Increment(ref _pendingCount) > _maxPending && _pending.TryDequeue(out _))
            {
                _ = Interlocked.Decrement(ref _pendingCount);
                long dropped = Interlocked.Increment(ref _droppedTotal);
                if (dropped == 1 || dropped % 1000 == 0)
                {
                    _logger.Warn("Очередь результатов переполнена (лимит {Max}) — всего вытеснено {Dropped}",
                        _maxPending, dropped);
                }
            }

            _dirty = true;
            EnsurePersistTimer();
            _ = _signal.Writer.TryWrite(true); // будим потребителя
        }

        /// <inheritdoc/>
        public async Task<ResultBatch> TakeBatchAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            await _takeLock.WaitAsync(cancellationToken);
            try
            {
                // Неподтверждённая пачка выдаётся повторно — сервер её ещё не записал.
                lock (_batchLock)
                {
                    if (_inFlight is not null)
                    {
                        return new ResultBatch { BatchId = _inFlightId, Items = [.. _inFlight] };
                    }
                }

                using CancellationTokenSource linked =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                linked.CancelAfter(timeout);

                List<ActionData> batch = DrainPending();
                if (batch.Count == 0)
                {
                    try
                    {
                        // Асинхронно ждём сигнала о новых данных — поток при этом свободен.
                        while (await _signal.Reader.WaitToReadAsync(linked.Token))
                        {
                            _ = _signal.Reader.TryRead(out _); // сбрасываем сигнал
                            batch = DrainPending();
                            if (batch.Count > 0)
                            {
                                break;
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // Истёк таймаут длинного опроса или клиент разорвал соединение — штатно.
                    }
                }

                if (batch.Count == 0)
                {
                    return new ResultBatch { BatchId = Guid.Empty, Items = [] };
                }

                Guid batchId = Guid.NewGuid();
                lock (_batchLock)
                {
                    _inFlightId = batchId;
                    _inFlight = batch;
                }

                _dirty = true;
                await FlushToDiskAsync();
                return new ResultBatch { BatchId = batchId, Items = [.. batch] };
            }
            finally
            {
                _ = _takeLock.Release();
            }
        }

        /// <inheritdoc/>
        public async Task<bool> ConfirmAsync(Guid batchId)
        {
            lock (_batchLock)
            {
                if (_inFlight is null || _inFlightId != batchId)
                {
                    return false; // пачка неизвестна или уже подтверждена
                }
                _inFlight = null;
                _inFlightId = Guid.Empty;
            }

            _dirty = true;
            await FlushToDiskAsync();
            return true;
        }

        /// <inheritdoc/>
        public async Task LoadAsync(CancellationToken cancellationToken)
        {
            if (!File.Exists(PersistenceFileName))
            {
                return;
            }

            try
            {
                string text = await File.ReadAllTextAsync(PersistenceFileName, cancellationToken);
                ActionData[]? saved = JsonConvert.DeserializeObject<ActionData[]>(text);
                foreach (ActionData item in saved ?? [])
                {
                    _pending.Enqueue(item);
                    _ = Interlocked.Increment(ref _pendingCount);
                }

                if (!_pending.IsEmpty)
                {
                    _ = _signal.Writer.TryWrite(true);
                    _logger.Info("Загружено {Count} недоставленных результатов из {File}", _pendingCount, PersistenceFileName);
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Не удалось загрузить сохранённые результаты из {File}", PersistenceFileName);
            }
        }

        /// <summary>Извлекает все накопленные на текущий момент результаты из очереди.</summary>
        private List<ActionData> DrainPending()
        {
            List<ActionData> batch = [];
            while (_pending.TryDequeue(out ActionData? item))
            {
                _ = Interlocked.Decrement(ref _pendingCount);
                batch.Add(item);
            }
            return batch;
        }

        /// <summary>Запускает фоновый таймер сохранения при первом появлении данных.</summary>
        private void EnsurePersistTimer()
        {
            if (_persistTimer != null || _disposed)
            {
                return;
            }

            Timer timer = new(async _ => await FlushToDiskAsync(), null, PersistInterval, PersistInterval);
            if (Interlocked.CompareExchange(ref _persistTimer, timer, null) != null)
            {
                timer.Dispose();
            }
        }

        /// <summary>
        /// Сохраняет на диск снимок недоставленных результатов: неподтверждённую пачку
        /// и очередь. После перезапуска всё возвращается в очередь на выдачу.
        /// </summary>
        private async Task FlushToDiskAsync()
        {
            if (!_dirty || _disposed)
            {
                return;
            }

            await _fileLock.WaitAsync();
            try
            {
                _dirty = false;

                List<ActionData> snapshot;
                lock (_batchLock)
                {
                    snapshot = _inFlight is null ? [] : [.. _inFlight];
                }
                snapshot.AddRange(_pending);

                if (snapshot.Count == 0)
                {
                    if (File.Exists(PersistenceFileName))
                    {
                        File.Delete(PersistenceFileName);
                    }
                    return;
                }

                await File.WriteAllTextAsync(PersistenceFileName, JsonConvert.SerializeObject(snapshot));
            }
            catch (Exception ex)
            {
                _dirty = true; // повторим попытку на следующем тике таймера
                _logger.Error(ex, "Ошибка сохранения результатов в {File}", PersistenceFileName);
            }
            finally
            {
                _ = _fileLock.Release();
            }
        }

        /// <summary>Освобождает таймер и примитивы синхронизации.</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _persistTimer?.Dispose();
            _fileLock.Dispose();
            _takeLock.Dispose();
        }
    }
}
