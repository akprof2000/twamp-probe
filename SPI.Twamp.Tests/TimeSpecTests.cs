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

        [Theory(DisplayName = "Компактная запись: одиночные буквы, в том числе слитно")]
        [InlineData("1y2mD", 1, 2, 1)]  // 1 год 2 месяца 1 день
        [InlineData("2Г3М1Д", 2, 3, 1)] // русские буквы с числами слитно
        [InlineData("ГмД", 1, 1, 1)]    // без чисел — по единице, регистр не важен
        [InlineData("м д", 0, 1, 1)]    // месяц и день по отдельности
        public void Duration_Compact(string text, int years, int months, int days)
        {
            DateTime result = TimeSpec.Resolve(text, Origin, Origin, out string? error);

            Assert.Null(error);
            Assert.Equal(Origin.AddYears(years).AddMonths(months).AddDays(days), result);
        }

        [Fact(DisplayName = "Компактная запись: «2д» — два дня")]
        public void Duration_Compact_TwoDays()
        {
            DateTime result = TimeSpec.Resolve("2д", Origin, Origin, out string? error);

            Assert.Null(error);
            Assert.Equal(Origin.AddDays(2), result);
        }

        [Fact(DisplayName = "Компактная запись: недели и часы «1н2ч»")]
        public void Duration_Compact_WeekHour()
        {
            DateTime result = TimeSpec.Resolve("1н2ч", Origin, Origin, out string? error);

            Assert.Null(error);
            Assert.Equal(Origin.AddDays(7).AddHours(2), result);
        }

        [Fact(DisplayName = "Слово из неизвестных букв не засчитывается частично")]
        public void Duration_UnknownWord_NotPartiallyApplied()
        {
            // «недождь» начинается с «д»-подобных букв, но целиком единицей не является.
            DateTime fallback = Origin.AddDays(1);
            DateTime result = TimeSpec.Resolve("дождь", Origin, fallback, out string? error);

            Assert.NotNull(error);
            Assert.Equal(fallback, result);
        }
    }
}
