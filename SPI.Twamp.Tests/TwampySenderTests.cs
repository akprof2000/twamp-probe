// Ignore Spelling: SPI Twamp twampy

using SPI.Twamp.Probe.Runners;
using SPI.Twamp.Server.Parser;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace SPI.Twamp.Tests
{
    /// <summary>
    /// Тесты встроенного C#-отправителя twampy: реальный обмен по UDP с локальным
    /// рефлектором и совместимость вывода с серверным <see cref="TwampyParser"/>.
    /// </summary>
    public class TwampySenderTests
    {
        /// <summary>
        /// Разница эпох NTP (1900) и Unix (1970) — рефлектор ставит метки в формате NTP,
        /// как ожидает отправитель.
        /// </summary>
        private const double TimeOffset = 2208988800.0;
        private const double AllBits = 4294967295.0;

        /// <summary>
        /// Минимальный TWAMP-Light рефлектор для теста: принимает тестовый пакет и
        /// отражает его с метками t2 (приём) и t3 (отправка) в раскладке, которую
        /// разбирает <see cref="TwampySender"/>. Возвращает свой порт и токен остановки.
        /// </summary>
        private static (int Port, CancellationTokenSource Cts, Task Loop) StartReflector()
        {
            Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            int port = ((IPEndPoint)socket.LocalEndPoint!).Port;
            CancellationTokenSource cts = new();

            Task loop = Task.Run(() =>
            {
                byte[] buffer = new byte[9216];
                uint rseq = 0;
                while (!cts.IsCancellationRequested)
                {
                    if (!socket.Poll(50_000, SelectMode.SelectRead))
                    {
                        continue;
                    }

                    EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                    int length;
                    try
                    {
                        length = socket.ReceiveFrom(buffer, ref from);
                    }
                    catch (SocketException)
                    {
                        break;
                    }
                    if (length < 14)
                    {
                        continue;
                    }

                    double t2 = (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds;
                    uint sseq = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(0, 4));
                    // t1 (метка отправителя) лежит в data[4:12] — отражаем её в data[28:36].
                    byte[] senderT1 = buffer.AsSpan(4, 8).ToArray();

                    double t3 = (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds;

                    // Ответный пакет минимум 36 байт по раскладке отправителя:
                    // [0:4] rseq, [4:12] t3, [16:24] t2, [24:28] sseq, [28:36] t1.
                    byte[] reply = new byte[36];
                    BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(0, 4), rseq++);
                    WriteNtp(reply.AsSpan(4, 8), t3);
                    WriteNtp(reply.AsSpan(16, 8), t2);
                    BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(24, 4), sseq);
                    senderT1.CopyTo(reply.AsSpan(28, 8));

                    _ = socket.SendTo(reply, from);
                }
                socket.Close();
            });

            return (port, cts, loop);
        }

        /// <summary>Записывает секунды Unix в 8-байтную метку NTP (big-endian).</summary>
        private static void WriteNtp(Span<byte> target, double unixSeconds)
        {
            BinaryPrimitives.WriteUInt32BigEndian(target[..4], (uint)(TimeOffset + Math.Floor(unixSeconds)));
            BinaryPrimitives.WriteUInt32BigEndian(target.Slice(4, 4), (uint)((unixSeconds - Math.Floor(unixSeconds)) * AllBits));
        }

        [Fact(DisplayName = "Встроенный sender обменивается с рефлектором и печатает таблицу")]
        public async Task Sender_Roundtrip_Produces_Table()
        {
            (int port, CancellationTokenSource cts, Task loop) = StartReflector();
            try
            {
                string args = $"sender 127.0.0.1:{port} :0 -c 5 -i 20";
                TwampySender.Result result = await TwampySender.RunAsync(args, TestContext.Current.CancellationToken);

                Assert.Equal("", result.Error);
                Assert.Contains("Direction", result.Output);
                Assert.Contains("Outbound:", result.Output);
                Assert.Contains("Roundtrip:", result.Output);
            }
            finally
            {
                cts.Cancel();
                await loop;
            }
        }

        [Fact(DisplayName = "Вывод встроенного sender разбирается серверным TwampyParser")]
        public async Task Output_Is_Parsed_By_Server()
        {
            (int port, CancellationTokenSource cts, Task loop) = StartReflector();
            try
            {
                string args = $"sender 127.0.0.1:{port} :0 -c 8 -i 20";
                TwampySender.Result result = await TwampySender.RunAsync(args, TestContext.Current.CancellationToken);

                // Тот же путь, что на сервере: разбор вывода twampy в статистику.
                List<TwPingStats> stats = TwampyParser.ParseMany(result.Output, result.Error, Guid.NewGuid());
                TwPingStats row = Assert.Single(stats);

                // Данные доехали: круговая задержка и джиттер посчитаны, потерь нет.
                Assert.NotNull(row.RttMedian);
                Assert.NotNull(row.TwoWayJitter);
                Assert.Equal(0, row.LossPercent);
                Assert.True(string.IsNullOrEmpty(row.Errors), "ошибок быть не должно");
            }
            finally
            {
                cts.Cancel();
                await loop;
            }
        }

        [Fact(DisplayName = "Полная потеря пакетов: печатается NO STATS, парсер даёт 100% потерь")]
        public async Task No_Reflector_Reports_Full_Loss()
        {
            // Рефлектор не поднят: на свободном порту ответов не будет.
            using Socket free = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            free.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            int deadPort = ((IPEndPoint)free.LocalEndPoint!).Port;
            free.Close();

            string args = $"sender 127.0.0.1:{deadPort} :0 -c 3 -i 20";
            TwampySender.Result result = await TwampySender.RunAsync(args, TestContext.Current.CancellationToken);

            Assert.Contains("NO STATS AVAILABLE", result.Output);
            TwPingStats row = Assert.Single(TwampyParser.ParseMany(result.Output, result.Error, Guid.NewGuid()));
            Assert.Equal(100, row.LossPercent);
        }

        [Theory(DisplayName = "FormatDuration повторяет единицы twampy (us/ms/sec/min)")]
        [InlineData(0.4, "us")]
        [InlineData(5.0, "ms")]
        [InlineData(2500.0, "sec")]
        [InlineData(120000.0, "min")]
        public void FormatDuration_Uses_Expected_Unit(double ms, string unit)
        {
            string formatted = TwampySender.FormatDuration(ms);
            Assert.EndsWith(unit, formatted);
            // Значение с единицей должно разбираться тем же способом, что применяет парсер.
            Assert.Matches(@"-?[\d.]+\s*(us|ms|sec|min)", formatted);
        }
    }
}
