// Ignore Spelling: SPI Twamp

namespace spi.twamp.server.Environment
{
    /// <summary>Короткие человекочитаемые описания исключений для журнала и ответов API.</summary>
    public static class ErrorText
    {
        /// <summary>
        /// Короткая причина ошибки: сообщение самого внутреннего исключения. Для сетевых
        /// сбоев (Flurl оборачивает HttpRequestException → SocketException) это и есть суть,
        /// например «Connection refused» или «No route to host».
        /// </summary>
        public static string ShortReason(Exception ex)
        {
            Exception current = ex;
            while (current.InnerException is not null)
            {
                current = current.InnerException;
            }
            return current.Message;
        }
    }
}
