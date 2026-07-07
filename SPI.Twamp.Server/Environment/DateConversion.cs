// Ignore Spelling: emr spi twamp

namespace spi.twamp.server.Environment
{
    /// <summary>
    /// 
    /// </summary>
    public static class DateConversion
    {
        /// <summary>
        /// Converts to date only.
        /// </summary>
        /// <param name="input">The input.</param>
        /// <returns></returns>
        public static DateOnly? ToDateOnly(this DateTime? input)
        {
            return input == null ? null : DateOnly.FromDateTime(input.Value);
        }
    }
}
