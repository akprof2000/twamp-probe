// Ignore Spelling: SPI Twamp

using Newtonsoft.Json;
using NLog;
using SPI.Twamp.Probe.Abstractions;
using SPI.Twamp.Probe.Contracts;
using System.Collections.Concurrent;
using System.Threading.Channels;

namespace SPI.Twamp.Probe.Server
{
    /// <summary>
    /// Реализация хранилища результатов на основе неблокирующей очереди и
    /// «сигнального» канала.
    /// <para>
    /// Тысячи параллельных задач могут добавлять результаты через <see cref="Add"/>,
    /// а веб-интерфейс забирает их пачками через <see cref="TakeBatchAsync"/>.
    /// Ожидание новых данных реализовано асинхронно, поэтому во время «длинного
    /// опроса» не удерживается ни один поток пула — именно это устраняло зависание
    /// ответа при запуске более 300 задач одновременно.
    /// </para>
    /// </summary>
    public sealed class ResultStore(Logger logger) : IResultStore, IDisposable
    {
        /// <summary>Файл для сохранения ещё не доставленных результатов между перезапусками.</summary>
        private const string PersistenceFileName = "JobResult.json";

        /// <summary>Интервал фонового сохранения снимка очереди на диск.</summary>
        private static readonly TimeSpan PersistInterval = TimeSpan.FromSeconds(1);

        private readonly Logger _logger = logger;

        /// <summary>Очередь накопленных результатов — единый источник истины для выдачи и сохранения.</summary>
        private readonly ConcurrentQueue<ActionData> _pending = new();

        /// <summary>
        /// Канал-сигнал ёмкостью 1 с режимом <see cref="BoundedChannelFullMode.DropWrite"/>.
        /// Работает как асинхронный AutoResetEvent: множество добавлений «схлопываются»
        /// в один сигнал, а ожидающий потребитель пробуждается без блокировки потока.
        /// </summary>
        private readonly Channel<bool> _signal =
            Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropWrite
            });

        /// <summary>Сериализует запись файла, чтобы избежать одновременных обращений к диску.</summary>
        private readonly SemaphoreSlim _fileLock = new(1, 1);

        /// <summary>Таймер фонового сохранения снимка очереди на диск. Создаётся лениво в <see cref="EnsurePersistTimer"/>.</summary>
        private Timer? _persistTimer;

        /// <summary>Признак наличия несохранённых изменений (снижает лишние записи на диск).</summary>
        private volatile bool _dirty;
        private volatile bool _disposed;

        /// <inheritdoc/>
        public void Add(ActionData result)
        {
            _pending.Enqueue(result);
            _dirty = true;
            EnsurePersistTimer();

            // Будим потребителя. TryWrite не блокирует и молча игнорируется,
            // если сигнал уже взведён (ёмкость канала равна 1).
            _ = _signal.Writer.TryWrite(true);
        }

        /// <inheritdoc/>
        public async Task<ActionData[]> TakeBatchAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            using CancellationTokenSource linked =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(timeout);

            // Сначала забираем то, что уже накопилось на текущий момент.
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
                    // Истёк таймаут длинного опроса или клиент разорвал соединение —
                    // это штатный сценарий, просто возвращаем то, что успели накопить.
                }
            }

            if (batch.Count > 0)
            {
                _dirty = true;
                await FlushToDiskAsync();
            }

            return [.. batch];
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
                }

                if (!_pending.IsEmpty)
                {
                    _ = _signal.Writer.TryWrite(true);
                    _logger.Info("Загружено {Count} недоставленных результатов из {File}", _pending.Count, PersistenceFileName);
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

            // Простой double-check без блокировки: гонка максимум создаст лишний таймер,
            // но такой сценарий крайне маловероятен и безопасен.
            Timer timer = new(async _ => await FlushToDiskAsync(), null, PersistInterval, PersistInterval);
            if (Interlocked.CompareExchange(ref _persistTimer, timer, null) != null)
            {
                timer.Dispose();
            }
        }

        /// <summary>
        /// Сохраняет текущий снимок очереди на диск (или удаляет файл, если очередь пуста).
        /// Вызывается по таймеру и при выдаче пачки, чтобы результаты пережили перезапуск.
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
                ActionData[] snapshot = [.. _pending];

                if (snapshot.Length == 0)
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

        /// <summary>Освобождает таймер и семафор.</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _persistTimer?.Dispose();
            _fileLock.Dispose();
        }
    }
}
