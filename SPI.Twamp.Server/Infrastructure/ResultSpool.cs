// Ignore Spelling: SPI Twamp Clickhouse ndjson

using NLog;
using spi.twamp.server.Environment;
using SPI.Twamp.Server.Abstractions;
using SPI.Twamp.Server.Contracts;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace SPI.Twamp.Server.Infrastructure
{
    /// <summary>
    /// Файловая реализация <see cref="IResultSpool"/>: каждый сегмент — отдельный файл NDJSON.
    /// <para>
    /// Почему файлы, а не коллекции в LiteDB: сегменты живут недолго и нужны только как
    /// очередь на отправку. Файл дописывается последовательно (дёшево), отправляется в
    /// ClickHouse потоком без разбора и удаляется целиком; база при этом не разрастается
    /// и не ловит блокировки при накоплении сотен тысяч записей.
    /// </para>
    /// </summary>
    public sealed class ResultSpool : IResultSpool, IDisposable
    {
        /// <summary>Имя файла заполняемого сегмента.</summary>
        private const string CurrentFileName = "current.ndjson";

        /// <summary>Шаблон поиска запечатанных сегментов.</summary>
        private const string SealedPattern = "seg-*.ndjson";

        private readonly Logger _logger;
        private readonly string _directory;
        private readonly int _maxRows;
        private readonly int _maxSegments;
        private readonly TimeSpan _maxAge;

        /// <summary>Один писатель на процесс: все обращения к текущему сегменту сериализуются.</summary>
        private readonly SemaphoreSlim _gate = new(1, 1);

        private StreamWriter? _writer;
        private int _currentRows;
        private DateTime _currentOpenedAt = DateTime.Now;
        private long _sequence;
        private int _sealedCount;

        /// <summary>Создаёт буфер и подхватывает сегменты, оставшиеся от прошлого запуска.</summary>
        /// <param name="logger">Логгер.</param>
        /// <param name="configuration">Конфигурация (секция <c>ClickHouse</c>).</param>
        public ResultSpool(Logger logger, IConfiguration configuration)
        {
            _logger = logger;
            _maxRows = Math.Max(1, configuration["ClickHouse:BatchRows"].ConvertTo(100_000));
            _maxSegments = Math.Max(1, configuration["ClickHouse:MaxSegments"].ConvertTo(16));
            _maxAge = TimeSpan.FromMinutes(Math.Max(1, configuration["ClickHouse:FlushMinutes"].ConvertTo(10)));

            string configured = configuration["ClickHouse:SpoolPath"] ?? "spool";
            _directory = Path.IsPathRooted(configured)
                ? configured
                : Path.Combine(AppContext.BaseDirectory, configured);
            _ = Directory.CreateDirectory(_directory);

            Recover();
        }

        /// <inheritdoc/>
        public bool IsFull => Volatile.Read(ref _sealedCount) >= _maxSegments;

        /// <inheritdoc/>
        public int SealedCount => Volatile.Read(ref _sealedCount);

        /// <inheritdoc/>
        public int CurrentRows => Volatile.Read(ref _currentRows);

        /// <inheritdoc/>
        public long PendingRows =>
            CurrentRows + GetSealedSegments().Sum(static segment => (long)segment.Rows);

        /// <inheritdoc/>
        public int MaxSegments => _maxSegments;

        /// <inheritdoc/>
        public int BatchRows => _maxRows;

        /// <inheritdoc/>
        public int FlushMinutes => (int)_maxAge.TotalMinutes;

        /// <summary>
        /// Восстанавливает состояние после перезапуска: считает уже запечатанные сегменты,
        /// продолжает нумерацию и запечатывает недописанный текущий сегмент, чтобы его
        /// содержимое не застряло в ожидании новых данных.
        /// </summary>
        private void Recover()
        {
            string[] sealedFiles = Directory.GetFiles(_directory, SealedPattern);
            _sealedCount = sealedFiles.Length;
            _sequence = sealedFiles.Select(ExtractSequence).DefaultIfEmpty(0).Max();

            string current = Path.Combine(_directory, CurrentFileName);
            if (File.Exists(current) && new FileInfo(current).Length > 0)
            {
                // Число строк неизвестно (счётчик жил в памяти) — считаем по файлу.
                int rows = File.ReadLines(current).Count();
                _ = SealFile(current, rows);
                _logger.Info("Буфер результатов: незавершённый сегмент запечатан после перезапуска, строк {Rows}", rows);
            }
            else if (File.Exists(current))
            {
                File.Delete(current); // пустой остаток — просто убираем
            }

            if (_sealedCount > 0)
            {
                _logger.Info("Буфер результатов: сегментов к отправке {Count}", _sealedCount);
            }
        }

        /// <summary>
        /// Разбирает имя файла сегмента <c>seg-{номер}-{строк}.ndjson</c>.
        /// Число строк вшито в имя, чтобы объём очереди был виден без чтения файлов.
        /// </summary>
        private static (long Sequence, int Rows) ParseName(string path)
        {
            string[] parts = Path.GetFileNameWithoutExtension(path).Split('-');
            long sequence = parts.Length > 1 && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long s) ? s : 0;
            int rows = parts.Length > 2 && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int r) ? r : 0;
            return (sequence, rows);
        }

        /// <summary>Порядковый номер сегмента — по нему определяется очередь отправки.</summary>
        private static long ExtractSequence(string path) => ParseName(path).Sequence;

        /// <inheritdoc/>
        public async Task AppendAsync(IReadOnlyList<ExportRow> rows, CancellationToken cancellationToken)
        {
            if (rows.Count == 0)
            {
                return;
            }

            await _gate.WaitAsync(cancellationToken);
            try
            {
                StreamWriter writer = EnsureWriter();
                foreach (ExportRow row in rows)
                {
                    await writer.WriteLineAsync(JsonSerializer.Serialize(row, SpoolJson.Options));
                }

                // Сбрасываем на диск до того, как вызывающий подтвердит пачку пробе:
                // подтверждаем только то, что уже пережило бы падение процесса.
                await writer.FlushAsync(cancellationToken);
                _currentRows += rows.Count;

                if (_currentRows >= _maxRows)
                {
                    SealCurrent("достигнут предел строк");
                }
            }
            finally
            {
                _ = _gate.Release();
            }
        }

        /// <inheritdoc/>
        public async Task<bool> SealIfDueAsync()
        {
            await _gate.WaitAsync();
            try
            {
                if (_currentRows == 0 || DateTime.Now - _currentOpenedAt < _maxAge)
                {
                    return false;
                }

                SealCurrent("истёк срок накопления");
                return true;
            }
            finally
            {
                _ = _gate.Release();
            }
        }

        /// <summary>Открывает (при необходимости) писателя текущего сегмента.</summary>
        private StreamWriter EnsureWriter()
        {
            if (_writer is not null)
            {
                return _writer;
            }

            // FileShare.ReadWrite — чтобы выгрузка отчёта могла читать заполняемый сегмент.
            FileStream stream = new(
                Path.Combine(_directory, CurrentFileName),
                FileMode.Append, FileAccess.Write, FileShare.ReadWrite);

            _writer = new StreamWriter(stream, new UTF8Encoding(false));
            _currentRows = 0;
            _currentOpenedAt = DateTime.Now;
            return _writer;
        }

        /// <summary>Закрывает текущий сегмент и переводит его в очередь на отправку.</summary>
        /// <param name="reason">Причина запечатывания — для журнала.</param>
        private void SealCurrent(string reason)
        {
            if (_writer is null)
            {
                return;
            }

            _writer.Dispose();
            _writer = null;

            string sealedPath = SealFile(Path.Combine(_directory, CurrentFileName), _currentRows);
            _logger.Info("Буфер результатов: сегмент {Segment} запечатан ({Reason}), строк {Rows}, в очереди {Queued}",
                Path.GetFileName(sealedPath), reason, _currentRows, _sealedCount);

            _currentRows = 0;
            _currentOpenedAt = DateTime.Now;
        }

        /// <summary>Переименовывает файл текущего сегмента в запечатанный и учитывает его в очереди.</summary>
        /// <param name="currentPath">Путь заполняемого сегмента.</param>
        /// <param name="rows">Число строк в нём — попадает в имя файла.</param>
        private string SealFile(string currentPath, int rows)
        {
            string target = Path.Combine(
                _directory, $"seg-{++_sequence:D9}-{rows}.ndjson");

            File.Move(currentPath, target);
            _ = Interlocked.Increment(ref _sealedCount);
            return target;
        }

        /// <inheritdoc/>
        public IReadOnlyList<SpoolSegment> GetSealedSegments()
        {
            string[] files = Directory.GetFiles(_directory, SealedPattern);
            Array.Sort(files, static (a, b) => ExtractSequence(a).CompareTo(ExtractSequence(b)));
            return [.. files.Select(static path => new SpoolSegment(path, ParseName(path).Rows))];
        }

        /// <inheritdoc/>
        public void DeleteSegment(string path)
        {
            File.Delete(path);
            _ = Interlocked.Decrement(ref _sealedCount);
        }

        /// <inheritdoc/>
        public async IAsyncEnumerable<ExportRow> ReadPendingAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            // Сначала запечатанные (более старые), затем то, что копится сейчас.
            List<string> files =
            [
                .. GetSealedSegments().Select(static segment => segment.Path),
                Path.Combine(_directory, CurrentFileName)
            ];

            foreach (string file in files)
            {
                if (!File.Exists(file))
                {
                    continue;
                }

                await foreach (ExportRow row in ReadFileAsync(file, cancellationToken))
                {
                    yield return row;
                }
            }
        }

        /// <summary>Читает строки одного файла сегмента.</summary>
        private async IAsyncEnumerable<ExportRow> ReadFileAsync(
            string path,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            // FileShare.ReadWrite — файл может дописываться параллельно.
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using StreamReader reader = new(stream, Encoding.UTF8);

            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                ExportRow? row = TryDeserialize(line, path);
                if (row is not null)
                {
                    yield return row;
                }
            }
        }

        /// <summary>Разбирает строку сегмента, пропуская повреждённую (обрыв записи при падении).</summary>
        private ExportRow? TryDeserialize(string line, string path)
        {
            try
            {
                return JsonSerializer.Deserialize<ExportRow>(line, SpoolJson.Options);
            }
            catch (JsonException ex)
            {
                _logger.Warn(ex, "Буфер результатов: пропущена повреждённая строка в {File}", Path.GetFileName(path));
                return null;
            }
        }

        /// <summary>Закрывает текущий сегмент, не теряя записанное.</summary>
        public void Dispose()
        {
            _writer?.Dispose();
            _writer = null;
            _gate.Dispose();
        }
    }
}
