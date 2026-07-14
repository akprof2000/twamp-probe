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

        [Fact(DisplayName = "Годы и месяцы применяются календарно")]
        public void Duration_YearMonth_Calendar()
        {
            DateTime result = TimeSpec.Resolve("1 year 2 month", Origin, Origin, out string? error);

            Assert.Null(error);
            Assert.Equal(Origin.AddYears(1).AddMonths(2), result);
        }

        [Fact(DisplayName = "Число можно опускать: «weak» — одна неделя")]
        public void Duration_BareUnit_MeansOne()
        {
            DateTime result = TimeSpec.Resolve("weak", Origin, Origin, out string? error);

            Assert.Null(error);
            Assert.Equal(Origin.AddDays(7), result);
        }

        [Fact(DisplayName = "«year month weak» — по единице каждого")]
        public void Duration_BareUnits_Combined()
        {
            DateTime result = TimeSpec.Resolve("year month weak", Origin, Origin, out string? error);

            Assert.Null(error);
            Assert.Equal(Origin.AddYears(1).AddMonths(1).AddDays(7), result);
        }

        [Fact(DisplayName = "Русские единицы со склонениями: «1 год 2 месяца 2 недели 15 дней»")]
        public void Duration_Russian_Declensions()
        {
            DateTime result = TimeSpec.Resolve("1 год 2 месяца 2 недели 15 дней", Origin, Origin, out string? error);

            Assert.Null(error);
            Assert.Equal(Origin.AddYears(1).AddMonths(2).AddDays(14 + 15), result);
        }

        [Fact(DisplayName = "«2 года 1 неделя день» — единица без числа в конце")]
        public void Duration_Russian_BareTrailingUnit()
        {
            DateTime result = TimeSpec.Resolve("2 года 1 неделя день", Origin, Origin, out string? error);

            Assert.Null(error);
            Assert.Equal(Origin.AddYears(2).AddDays(7 + 1), result);
        }

        [Fact(DisplayName = "«5 лет» — форма «лет» понимается как годы")]
        public void Duration_Russian_Let()
        {
            DateTime result = TimeSpec.Resolve("5 лет", Origin, Origin, out string? error);

            Assert.Null(error);
            Assert.Equal(Origin.AddYears(5), result);
        }

        [Fact(DisplayName = "Часы, минуты, секунды по-русски: «3 часа 30 минут 15 секунд»")]
        public void Duration_Russian_TimeUnits()
        {
            DateTime result = TimeSpec.Resolve("3 часа 30 минут 15 секунд", Origin, Origin, out string? error);

            Assert.Null(error);
            Assert.Equal(Origin.AddHours(3).AddMinutes(30).AddSeconds(15), result);
        }

        [Fact(DisplayName = "Связки между компонентами пропускаются: «2 недели и 3 дня»")]
        public void Duration_ConnectorsIgnored()
        {
            DateTime result = TimeSpec.Resolve("2 недели и 3 дня", Origin, Origin, out string? error);

            Assert.Null(error);
            Assert.Equal(Origin.AddDays(17), result);
        }
    }
}
