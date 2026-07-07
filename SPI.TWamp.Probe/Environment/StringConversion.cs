// Ignore Spelling: emr spi twamp

using System.ComponentModel;

namespace spi.twamp.Probe.Environment
{
    /// <summary>
    /// Расширения для безопасного преобразования строк (например, значений конфигурации).
    /// </summary>
    public static class StringConversion
    {
        /// <summary>
        /// Преобразует строку к типу <typeparamref name="T"/>; при ошибке или null
        /// возвращает значение по умолчанию.
        /// </summary>
        /// <typeparam name="T">Целевой тип.</typeparam>
        /// <param name="value">Исходная строка.</param>
        /// <param name="def">Значение по умолчанию.</param>
        /// <returns>Преобразованное значение или <paramref name="def"/>.</returns>
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
