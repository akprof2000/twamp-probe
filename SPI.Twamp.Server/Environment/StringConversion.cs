// Ignore Spelling: emr spi twamp

using System.ComponentModel;

namespace spi.twamp.server.Environment
{
    /// <summary>
    /// 
    /// </summary>
    public static class StringConversion
    {
        /// <summary>
        /// 
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="value"></param>
        /// <param name="def"></param>
        /// <returns></returns>
        public static T? ConvertTo<T>(this string? value, T def)
        {
            if (value is null)
            {
                return def;

            }

            try
            {
                TypeConverter typeConverter = TypeDescriptor.GetConverter(typeof(T));
                object? propValue = typeConverter.ConvertFromString(value);
                return (T?)propValue;
            }
            catch
            {
                return def;
            }
        }

    }
}
