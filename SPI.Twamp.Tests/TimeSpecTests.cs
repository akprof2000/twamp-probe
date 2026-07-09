// Ignore Spelling: SPI Twamp

using SPI.Twamp.Server.Application;
using Xunit;

namespace SPI.Twamp.Tests
{
    /// <summary>
    /// Тесты разбора моментов времени шаблонов (даты и относительные длительности).
    /// </summary>
    public class TimeSpecTests
    {
        private static readonly DateTime Origin = new(2026, 7, 8, 10, 0, 0, DateTimeKind.Local);

        [Fact(DisplayName = "Пустое значение — возвращается fallback без ошибки")]
        public void Empty_ReturnsFallback()
        {
            DateTime result = TimeSpec.Resolve("", Origin, Origin.AddDays(14), out string? error);

            Assert.Null(error);
            Assert.Equal(Origin.AddDays(14), result);
        }

        [Fact(DisplayName = "Абсолютная дата в формате dd.MM.yyyy HH:mm")]
        public void AbsoluteDate_Parsed()
        {
            DateTime result = TimeSpec.Resolve("25.12.2026 10:30", Origin, Origin, out string? error);

            Assert.Null(error);
            Assert.Equal(new DateTime(2026, 12, 25, 10, 30, 0), result);
            Assert.Equal(DateTimeKind.Local, result.Kind);
        }

        [Fact(DisplayName = "Длительность «2 week 3 day 2 hour» — смещение от опорного момента")]
        public void Duration_WeeksDaysHours()
        {
            DateTime result = TimeSpec.Resolve("2 week 3 day 2 hour", Origin, Origin, out string? error);

            Assert.Null(error);
            Assert.Equal(Origin.AddDays(17).AddHours(2), result);
        }

        [Fact(DisplayName = "Опечатка «weak» понимается как week")]
        public void Duration_WeakTypo()
        {
            DateTime result = TimeSpec.Resolve("1 weak", Origin, Origin, out string? error);

            Assert.Null(error);
            Assert.Equal(Origin.AddDays(7), result);
        }

        [Fact(DisplayName = "Минуты и секунды поддерживаются")]
        public void Duration_MinutesSeconds()
        {
            DateTime result = TimeSpec.Resolve("30 min 15 sec", Origin, Origin, out string? error);

            Assert.Null(error);
            Assert.Equal(Origin.AddMinutes(30).AddSeconds(15), result);
        }

        [Fact(DisplayName = "Нечитаемое значение — ошибка и fallback")]
        public void Garbage_ReturnsError()
        {
            DateTime fallback = Origin.AddDays(1);
            DateTime result = TimeSpec.Resolve("абракадабра", Origin, fallback, out string? error);

            Assert.NotNull(error);
            Assert.Equal(fallback, result);
        }
    }
}
