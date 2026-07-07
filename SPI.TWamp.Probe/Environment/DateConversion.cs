// Ignore Spelling: emr spi twamp

namespace spi.twamp.Probe.Environment
{
    /// <summary>
    /// Расширения для преобразования дат.
    /// </summary>
    public static class DateConversion
    {
        /// <summary>
        /// Преобразует дату со временем в дату без времени.
        /// </summary>
        /// <param name="input">Исходная дата со временем.</param>
        /// <returns>Дата без времени или <c>null</c>, если вход был <c>null</c>.</returns>
        public static DateOnly? ToDateOnly(this DateTime? input)
        {
            return input == null ? null : DateOnly.FromDateTime(input.Value);
        }
    }
}
