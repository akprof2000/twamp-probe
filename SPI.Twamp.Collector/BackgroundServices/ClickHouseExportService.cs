// Ignore Spelling: SPI Twamp Clickhouse

using NLog;
using spi.twamp.server.Environment;
using SPI.Twamp.Server.Abstractions;
using SPI.Twamp.Server.Contracts;

namespace SPI.Twamp.Server.BackgroundServices
{
    /// <summary>
    /// Переносит накопленные результаты в ClickHouse: запечатывает текущий сегмент по
    /// истечении срока накопления и отправляет запечатанные сегменты по порядку, удаляя
    /// каждый сразу после успешной вставки.
    /// <para>
    /// Пока база недоступна, сегменты копятся в буфере; при достижении настроенного
    /// предела буфер сообщает о переполнении, и опрос проб приостанавливается —
    /// результаты остаются на пробах и будут забраны после восстановления связи.
    /// </para>
    /// </summary>
    public sealed class ClickHouseExportService(
        Logger logger, IResultSpool spool, IClickHouseWriter writer, IConfiguration configuration)
        : BackgroundService, IClickHouseStatusProvider
    {
        /// <summary>Как часто проверять буфер (срок накопления проверяется здесь же).</summary>
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

        private readonly Logger _logger = logger;
        private readonly IResultSpool _spool = spool;
        private readonly IClickHouseWriter _writer = writer;

        /// <summary>Пауза после ошибки базы, секунд.</summary>
        private readonly int _retrySeconds = configuration["ClickHouse:RetrySeconds"].ConvertTo(30);

        /// <summary>Таблица уже создана в текущем сеансе связи с базой.</summary>
        private bool _tableReady;

        /// <summary>О недоступности базы сообщаем один раз — до восстановления.</summary>
        private bool _reportedFailure;

        // --- Показатели для вкладки «Хранилище» веб-интерфейса ---

        /// <summary>Итог последнего обращения к базе; <c>null</c> — обращений ещё не было.</summary>
        private bool? _online;

        /// <summary>Текст последней ошибки базы и время её появления.</summary>
        private string? _lastError;
        private DateTime? _lastErrorAt;

        /// <summary>Время последней успешной выгрузки сегмента.</summary>
        private DateTime? _lastUploadAt;

        /// <summary>Накопительные счётчики выгруженного с момента запуска сервера.</summary>
        private long _segmentsUploaded;
        private long _rowsUploaded;

        /// <inheritdoc/>
        public ClickHouseState GetState() => new(
            Enabled: _writer.Enabled,
            Url: _writer.Url,
            Target: _writer.Target,
            Online: _online,
            LastError: _lastError,
            LastErrorAt: _lastErrorAt,
            LastUploadAt: _lastUploadAt,
            SegmentsUploaded: Interlocked.Read(ref _segmentsUploaded),
            RowsUploaded: Interlocked.Read(ref _rowsUploaded),
            CurrentRows: _spool.CurrentRows,
            PendingRows: _spool.PendingRows,
            SealedSegments: _spool.SealedCount,
            MaxSegments: _spool.MaxSegments,
            BatchRows: _spool.BatchRows,
            FlushMinutes: _spool.FlushMinutes,
            Backpressured: _spool.IsFull);

        /// <inheritdoc/>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_writer.Enabled)
            {
                _logger.Info("Выгрузка в ClickHouse выключена (ClickHouse:Enabled=false)");
                return;
            }

            _logger.Info("Служба выгрузки в ClickHouse запущена");

            // Проверяем связь и создаём схему сразу: так таблица появляется до первых
            // данных, а вкладка «Хранилище» показывает реальное состояние базы, а не
            // «обращений ещё не было» до первой выгрузки.
            await RunCycleAsync(stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                TimeSpan delay = await RunCycleAsync(stoppingToken) ? PollInterval : TimeSpan.FromSeconds(_retrySeconds);
                try
                {
                    await Task.Delay(delay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        /// <summary>
        /// Один цикл: запечатать просроченный сегмент и отправить очередь.
        /// Возвращает <c>false</c>, если база недоступна и нужна длинная пауза.
        /// </summary>
        private async Task<bool> RunCycleAsync(CancellationToken stoppingToken)
        {
            try
            {
                _ = await _spool.SealIfDueAsync();

                if (!_tableReady)
                {
                    await _writer.EnsureTableAsync(stoppingToken);
                    _tableReady = true;
                }

                await FlushSegmentsAsync(stoppingToken);
                _online = true;
                _lastError = null;
                return true;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                HandleFailure(ex);
                return false;
            }
        }

        /// <summary>Отправляет запечатанные сегменты по порядку, удаляя каждый после вставки.</summary>
        private async Task FlushSegmentsAsync(CancellationToken stoppingToken)
        {
            IReadOnlyList<SpoolSegment> segments = _spool.GetSealedSegments();
            if (segments.Count == 0)
            {
                return;
            }

            foreach (SpoolSegment segment in segments)
            {
                stoppingToken.ThrowIfCancellationRequested();

                await _writer.InsertSegmentAsync(segment.Path, stoppingToken);

                // Удаляем только после подтверждённой вставки: при сбое сегмент
                // отправится заново, а ReplacingMergeTree схлопнет повтор.
                _spool.DeleteSegment(segment.Path);

                _lastUploadAt = DateTime.Now;
                _ = Interlocked.Increment(ref _segmentsUploaded);
                _ = Interlocked.Add(ref _rowsUploaded, segment.Rows);

                _logger.Info("ClickHouse: сегмент {Segment} выгружен ({Rows} строк), осталось {Left}",
                    Path.GetFileName(segment.Path), segment.Rows, _spool.SealedCount);
            }

            if (_reportedFailure)
            {
                _logger.Info("ClickHouse: связь восстановлена, очередь разобрана");
                _reportedFailure = false;
            }
        }

        /// <summary>Обрабатывает недоступность базы: сообщает один раз и сбрасывает готовность таблицы.</summary>
        private void HandleFailure(Exception ex)
        {
            _tableReady = false; // база могла быть пересоздана — проверим схему заново
            _online = false;
            _lastError = ex.Message;
            _lastErrorAt = DateTime.Now;

            if (_reportedFailure)
            {
                _logger.Debug(ex, "ClickHouse по-прежнему недоступен");
                return;
            }

            _reportedFailure = true;
            _logger.Error(ex, "ClickHouse недоступен: сегментов в очереди {Queued}, повтор через {Delay} c",
                _spool.SealedCount, _retrySeconds);
        }
    }
}
