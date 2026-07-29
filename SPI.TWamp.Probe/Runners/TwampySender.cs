// Ignore Spelling: SPI Twamp twampy ntp

using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SPI.Twamp.Probe.Runners
{
    /// <summary>
    /// Встроенный отправитель TWAMP-Light (эксперимент): порт режима <c>sender</c> из
    /// nokia/twampy на C#, работающий прямо в процессе пробы — без запуска внешнего
    /// python-процесса. Формат тестовых пакетов, вычисление задержек, джиттера и потерь,
    /// а также текст итоговой таблицы воспроизведены так, чтобы серверный
    /// <c>TwampyParser</c> разбирал вывод один-в-один с оригиналом.
    /// <para>
    /// Смысл эксперимента — производительность: убираются накладные расходы на запуск
    /// процесса и старт интерпретатора python на каждый замер.
    /// </para>
    /// </summary>
    public static class TwampySender
    {
        /// <summary>Разница между эпохами NTP (1900) и Unix (1970), секунд.</summary>
        private const double TimeOffset = 2208988800.0;

        /// <summary>Маска 32-битной дробной части секунды NTP.</summary>
        private const double AllBits = 4294967295.0; // 0xFFFFFFFF

        /// <summary>Порт рефлектора по умолчанию (far-end).</summary>
        private const int DefaultFarPort = 20001;

        /// <summary>Единая привязка часов к абсолютному времени (аналог time0 в twampy).</summary>
        private static readonly double TimeZero =
            (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds - System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

        /// <summary>Текущее время в секундах Unix с высоким разрешением.</summary>
        private static double Now() =>
            TimeZero + System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

        /// <summary>Результат прогона: стандартный вывод (таблица) и текст ошибки.</summary>
        /// <param name="Output">Итоговая таблица — как печатает twampy sender.</param>
        /// <param name="Error">Текст ошибки (пусто при успехе).</param>
        public readonly record struct Result(string Output, string Error);

        /// <summary>
        /// Запускает замер по строке аргументов вида
        /// <c>[-m twampy] sender &lt;far&gt; [&lt;near&gt;] [-c N] [-i мс] [--padding B] [--tos T] [--ttl H]</c>.
        /// Незнакомые аргументы игнорируются — как у оригинала при вызове из пробы.
        /// </summary>
        /// <param name="arguments">Строка аргументов (та же, что уходила бы python).</param>
        /// <param name="cancellationToken">Токен отмены (таймаут задачи или остановка службы).</param>
        public static async Task<Result> RunAsync(string arguments, CancellationToken cancellationToken)
        {
            SenderOptions options;
            try
            {
                options = SenderOptions.Parse(arguments);
            }
            catch (Exception ex)
            {
                return new Result("", $"Некорректные аргументы twampy sender: {ex.Message}");
            }

            try
            {
                string table = await Task.Run(() => RunSession(options, cancellationToken), cancellationToken);
                return new Result(table, "");
            }
            catch (OperationCanceledException)
            {
                throw; // таймаут/остановку обрабатывает вызывающий код
            }
            catch (Exception ex)
            {
                return new Result("", $"Ошибка встроенного twampy sender: {ex.Message}");
            }
        }

        /// <summary>Проводит один сеанс отправителя и возвращает текст итоговой таблицы.</summary>
        private static string RunSession(SenderOptions options, CancellationToken cancellationToken)
        {
            using Socket socket = new(options.RemoteEndPoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            TrySetSocketOptions(socket, options);
            socket.Bind(new IPEndPoint(
                options.RemoteEndPoint.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any,
                options.LocalPort));

            TwampStatistics stats = new();
            byte[] recvBuffer = new byte[9216];

            double schedule = Now();
            double endTime = schedule + options.Count * (options.IntervalMs / 1000.0) + 5.0;
            int idx = 0;
            bool running = true;

            while (running)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Забираем все уже пришедшие ответы (неблокирующе).
                while (socket.Poll(0, SelectMode.SelectRead))
                {
                    double t4 = Now();
                    int length;
                    try
                    {
                        EndPoint from = new IPEndPoint(
                            options.RemoteEndPoint.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
                        length = socket.ReceiveFrom(recvBuffer, ref from);
                    }
                    catch (SocketException)
                    {
                        break; // порт временно недоступен — попробуем в следующей итерации
                    }

                    if (length < 36)
                    {
                        continue; // короткий пакет — не ответ рефлектора
                    }

                    double t3 = NtpToSeconds(recvBuffer.AsSpan(4, 8));   // время отправки рефлектором
                    double t2 = NtpToSeconds(recvBuffer.AsSpan(16, 8));  // время приёма рефлектором
                    double t1 = NtpToSeconds(recvBuffer.AsSpan(28, 8));  // наше время отправки (эхо)

                    double delayRt = Math.Max(0, 1000 * (t4 - t1 + t2 - t3));
                    double delayOb = Math.Max(0, 1000 * (t2 - t1));
                    double delayIb = Math.Max(0, 1000 * (t4 - t3));

                    uint rseq = BinaryPrimitives.ReadUInt32BigEndian(recvBuffer.AsSpan(0, 4));
                    uint sseq = BinaryPrimitives.ReadUInt32BigEndian(recvBuffer.AsSpan(24, 4));

                    stats.Add(delayRt, delayOb, delayIb, rseq, sseq);

                    if (sseq + 1 == (uint)options.Count)
                    {
                        running = false;
                    }
                }

                double sendTime = Now();
                if (sendTime >= schedule && idx < options.Count)
                {
                    schedule += options.IntervalMs / 1000.0;
                    SendPacket(socket, options, idx, sendTime);
                    idx++;

                    if (schedule > sendTime)
                    {
                        int waitMs = (int)Math.Ceiling((schedule - sendTime) * 1000);
                        if (waitMs > 0)
                        {
                            _ = socket.Poll(waitMs * 1000, SelectMode.SelectRead); // микросекунды
                        }
                    }
                }

                if (sendTime > endTime)
                {
                    running = false;
                }
            }

            return stats.Dump(idx);
        }

        /// <summary>Отправляет один тестовый пакет (формат TWAMP-Light sender).</summary>
        private static void SendPacket(Socket socket, SenderOptions options, int seq, double t1)
        {
            // Заголовок 14 байт: seq(4) + NTP секунды(4) + NTP дробь(4) + оценка ошибки 0x3FFF(2).
            int padding = options.NextPadding();
            byte[] packet = new byte[14 + padding];
            BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(0, 4), (uint)seq);
            BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4, 4), (uint)(TimeOffset + Math.Floor(t1)));
            BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8, 4), (uint)((t1 - Math.Floor(t1)) * AllBits));
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(12, 2), 0x3FFF);
            // Остаток пакета — нули (паддинг), массив уже обнулён.

            _ = socket.SendTo(packet, options.RemoteEndPoint);
        }

        /// <summary>Преобразует 8-байтную метку NTP (сек+дробь, big-endian) в секунды Unix.</summary>
        private static double NtpToSeconds(ReadOnlySpan<byte> ntp)
        {
            uint seconds = BinaryPrimitives.ReadUInt32BigEndian(ntp[..4]);
            uint fraction = BinaryPrimitives.ReadUInt32BigEndian(ntp.Slice(4, 4));
            return seconds - TimeOffset + fraction / AllBits;
        }

        /// <summary>Пытается выставить TOS/TTL сокета; на неподдерживающих ОС молча пропускает.</summary>
        private static void TrySetSocketOptions(Socket socket, SenderOptions options)
        {
            try
            {
                if (socket.AddressFamily == AddressFamily.InterNetwork)
                {
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.TypeOfService, options.Tos);
                    socket.Ttl = (short)options.Ttl;
                }
            }
            catch (SocketException) { /* параметр не поддержан — не критично для замера */ }
            catch (ArgumentException) { /* значение вне диапазона — оставляем по умолчанию */ }
        }

        /// <summary>Формат длительности из twampy: min/sec/ms/us по величине (для совместимости парсера).</summary>
        public static string FormatDuration(double ms)
        {
            double abs = Math.Abs(ms);
            if (abs > 60000)
            {
                return (ms / 60000).ToString("0.0", CultureInfo.InvariantCulture).PadLeft(7) + "min";
            }
            if (abs > 10000)
            {
                return (ms / 1000).ToString("0.0", CultureInfo.InvariantCulture).PadLeft(7) + "sec";
            }
            if (abs > 1000)
            {
                return (ms / 1000).ToString("0.00", CultureInfo.InvariantCulture).PadLeft(7) + "sec";
            }
            if (abs > 1)
            {
                return ms.ToString("0.00", CultureInfo.InvariantCulture).PadLeft(8) + "ms";
            }
            return ((int)(ms * 1000)).ToString(CultureInfo.InvariantCulture).PadLeft(8) + "us";
        }

        /// <summary>
        /// Накопитель статистики сеанса — точный порт <c>TwampStatistics</c> из twampy:
        /// те же формулы min/max/avg, джиттера [RFC1889] и потерь по направлениям.
        /// </summary>
        private sealed class TwampStatistics
        {
            private int _count;
            private double _minOb, _minIb, _minRt;
            private double _maxOb, _maxIb, _maxRt;
            private double _sumOb, _sumIb, _sumRt;
            private double _jitterOb, _jitterIb, _jitterRt;
            private double _lastOb, _lastIb, _lastRt;
            private long _lossIb, _lossOb;

            /// <summary>Добавляет один ответ: задержки RT/OB/IB и последовательности рефлектора/отправителя.</summary>
            public void Add(double delayRt, double delayOb, double delayIb, uint rseq, uint sseq)
            {
                if (_count == 0)
                {
                    _minOb = _maxOb = _sumOb = _lastOb = delayOb;
                    _minIb = _maxIb = _sumIb = _lastIb = delayIb;
                    _minRt = _maxRt = _sumRt = _lastRt = delayRt;
                    _lossIb = rseq;
                    _lossOb = (long)sseq - rseq;
                    _jitterOb = _jitterIb = _jitterRt = 0;
                }
                else
                {
                    _minOb = Math.Min(_minOb, delayOb);
                    _minIb = Math.Min(_minIb, delayIb);
                    _minRt = Math.Min(_minRt, delayRt);
                    _maxOb = Math.Max(_maxOb, delayOb);
                    _maxIb = Math.Max(_maxIb, delayIb);
                    _maxRt = Math.Max(_maxRt, delayRt);
                    _sumOb += delayOb;
                    _sumIb += delayIb;
                    _sumRt += delayRt;
                    _lossIb = (long)rseq - _count;
                    _lossOb = (long)sseq - rseq;

                    if (_count == 1)
                    {
                        _jitterOb = Math.Abs(_lastOb - delayOb);
                        _jitterIb = Math.Abs(_lastIb - delayIb);
                        _jitterRt = Math.Abs(_lastRt - delayRt);
                    }
                    else
                    {
                        _jitterOb += (Math.Abs(_lastOb - delayOb) - _jitterOb) / 16;
                        _jitterIb += (Math.Abs(_lastIb - delayIb) - _jitterIb) / 16;
                        _jitterRt += (Math.Abs(_lastRt - delayRt) - _jitterRt) / 16;
                    }

                    _lastOb = delayOb;
                    _lastIb = delayIb;
                    _lastRt = delayRt;
                }

                _count++;
            }

            /// <summary>Печатает таблицу направлений — тот же текст, что и twampy sender.</summary>
            /// <param name="total">Сколько пакетов было отправлено (для процента потерь).</param>
            public string Dump(int total)
            {
                const string bar = "===============================================================================";
                const string dash = "-------------------------------------------------------------------------------";
                StringBuilder sb = new();
                _ = sb.Append(bar).Append('\n');
                _ = sb.Append("Direction         Min         Max         Avg          Jitter     Loss").Append('\n');
                _ = sb.Append(dash).Append('\n');

                if (_count > 0 && total > 0)
                {
                    long lossRt = total - _count;
                    _ = sb.Append("  Outbound:    ").Append(Row(_minOb, _maxOb, _sumOb / _count, _jitterOb, _lossOb, total)).Append('\n');
                    _ = sb.Append("  Inbound:     ").Append(Row(_minIb, _maxIb, _sumIb / _count, _jitterIb, _lossIb, total)).Append('\n');
                    _ = sb.Append("  Roundtrip:   ").Append(Row(_minRt, _maxRt, _sumRt / _count, _jitterRt, lossRt, total)).Append('\n');
                }
                else
                {
                    _ = sb.Append("  NO STATS AVAILABLE (100% loss)").Append('\n');
                }

                _ = sb.Append(dash).Append('\n');
                _ = sb.Append("                                                    Jitter Algorithm [RFC1889]").Append('\n');
                _ = sb.Append(bar).Append('\n');
                return sb.ToString();
            }

            /// <summary>Собирает одну строку направления: min/max/avg/jitter + процент потерь.</summary>
            private static string Row(double min, double max, double avg, double jitter, long loss, int total)
            {
                double lossPercent = 100.0 * loss / total;
                return $"{FormatDuration(min)}  {FormatDuration(max)}  {FormatDuration(avg)}  {FormatDuration(jitter)}    "
                     + lossPercent.ToString("0.0", CultureInfo.InvariantCulture).PadLeft(5) + "%";
            }
        }

        /// <summary>Разобранные параметры отправителя.</summary>
        private sealed class SenderOptions
        {
            public required IPEndPoint RemoteEndPoint { get; init; }
            public required int LocalPort { get; init; }
            public required int Count { get; init; }
            public required int IntervalMs { get; init; }
            public required int Tos { get; init; }
            public required int Ttl { get; init; }
            private int[] Padmix { get; init; } = [0];
            private readonly Random _random = new();

            /// <summary>Случайный размер паддинга из набора (как padmix в twampy).</summary>
            public int NextPadding() => Padmix[_random.Next(Padmix.Length)];

            /// <summary>Разбирает строку аргументов sender'а в параметры.</summary>
            public static SenderOptions Parse(string arguments)
            {
                string[] tokens = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                int count = 100, interval = 100, padding = 0, tos = 0x88, ttl = 64;
                List<string> positionals = [];

                for (int i = 0; i < tokens.Length; i++)
                {
                    switch (tokens[i])
                    {
                        case "-m" or "twampy" or "sender":
                            break; // префикс запуска — пропускаем
                        case "-c" or "--count":
                            count = NextInt(tokens, ref i, count);
                            break;
                        case "-i" or "--interval":
                            interval = NextInt(tokens, ref i, interval);
                            break;
                        case "--padding":
                            padding = NextInt(tokens, ref i, padding);
                            break;
                        case "--tos":
                            tos = NextInt(tokens, ref i, tos);
                            break;
                        case "--ttl":
                            ttl = NextInt(tokens, ref i, ttl);
                            break;
                        case "-d" or "-v" or "-q" or "--do-not-fragment":
                            break; // флаги без значения — не влияют на замер
                        default:
                            if (!tokens[i].StartsWith('-'))
                            {
                                positionals.Add(tokens[i]);
                            }
                            break;
                    }
                }

                if (positionals.Count == 0)
                {
                    throw new ArgumentException("не указан адрес рефлектора (far-end)");
                }

                (string farIp, int farPort) = ParseAddress(positionals[0], DefaultFarPort);
                int localPort = 0; // near-end по умолчанию :0 (эфемерный порт)
                if (positionals.Count > 1)
                {
                    (_, localPort) = ParseAddress(positionals[1], 0);
                }

                IPAddress address = ResolveAddress(farIp);
                int[] padmix = padding > 0
                    ? [padding]
                    : address.AddressFamily == AddressFamily.InterNetworkV6
                        ? [0, 0, 0, 0, 0, 0, 0, 514, 514, 514, 514, 1438]
                        : [8, 8, 8, 8, 8, 8, 8, 534, 534, 534, 534, 1458];

                return new SenderOptions
                {
                    RemoteEndPoint = new IPEndPoint(address, farPort),
                    LocalPort = localPort,
                    Count = Math.Clamp(count, 1, 9999),
                    IntervalMs = Math.Max(1, interval),
                    Tos = tos,
                    Ttl = Math.Clamp(ttl, 1, 255),
                    Padmix = padmix
                };
            }

            /// <summary>Читает целочисленное значение следующего токена, сдвигая индекс.</summary>
            private static int NextInt(string[] tokens, ref int i, int fallback)
            {
                if (i + 1 < tokens.Length && int.TryParse(tokens[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                {
                    i++;
                    return value;
                }
                return fallback;
            }

            /// <summary>Разбирает «ip:port» / «[ipv6]:port» / «ip» в адрес и порт.</summary>
            private static (string Ip, int Port) ParseAddress(string addr, int defaultPort)
            {
                if (string.IsNullOrEmpty(addr) || addr == ":0")
                {
                    return ("", 0);
                }
                if (addr.StartsWith(':') && int.TryParse(addr[1..], out int onlyPort))
                {
                    return ("", onlyPort); // «:port» — только локальный порт
                }
                if (addr.Contains("]:"))
                {
                    int idx = addr.LastIndexOf(':');
                    return (addr[..idx].Trim('[', ']'), int.Parse(addr[(idx + 1)..], CultureInfo.InvariantCulture));
                }
                if (addr.Contains(']') || addr.Count(c => c == ':') > 1)
                {
                    return (addr.Trim('[', ']'), defaultPort); // IPv6 без порта
                }
                if (addr.Contains(':'))
                {
                    string[] parts = addr.Split(':');
                    return (parts[0], int.Parse(parts[1], CultureInfo.InvariantCulture));
                }
                return (addr, defaultPort);
            }

            /// <summary>Преобразует адрес рефлектора в <see cref="IPAddress"/> (с DNS-резолвингом по имени).</summary>
            private static IPAddress ResolveAddress(string ip)
            {
                if (string.IsNullOrEmpty(ip))
                {
                    return IPAddress.Loopback;
                }
                if (IPAddress.TryParse(ip, out IPAddress? parsed))
                {
                    return parsed;
                }
                // Имя хоста — берём первый адрес (IPv4 в приоритете).
                IPAddress[] resolved = Dns.GetHostAddresses(ip);
                return Array.Find(resolved, a => a.AddressFamily == AddressFamily.InterNetwork)
                    ?? resolved[0];
            }
        }
    }
}
