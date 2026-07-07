// Ignore Spelling: SPI Twamp

using SPI.Twamp.Probe.Contracts;

namespace SPI.Twamp.Probe.Abstractions
{
    /// <summary>
    /// Потокобезопасное хранилище результатов зондирования.
    /// <para>
    /// Отвечает за накопление результатов от множества параллельных задач
    /// и их асинхронную выдачу веб-интерфейсу через «длинный опрос» (long polling)
    /// без блокировки потоков пула — это ключ к устойчивой работе при большом
    /// числе одновременных задач.
    /// </para>
    /// </summary>
    public interface IResultStore
    {
        /// <summary>
        /// Добавляет результат выполнения задачи в очередь на выдачу.
        /// Метод неблокирующий и безопасен для вызова из множества потоков.
        /// </summary>
        /// <param name="result">Результат одного замера зонда.</param>
        void Add(ActionData result);

        /// <summary>
        /// Асинхронно ожидает появления результатов и возвращает накопленную пачку.
        /// Ожидание не удерживает поток пула и прекращается по таймауту или отмене.
        /// </summary>
        /// <param name="timeout">Максимальное время ожидания новых результатов.</param>
        /// <param name="cancellationToken">Токен отмены (например, разрыв соединения клиентом).</param>
        /// <returns>Массив накопленных результатов (возможно пустой при таймауте).</returns>
        Task<ActionData[]> TakeBatchAsync(TimeSpan timeout, CancellationToken cancellationToken);

        /// <summary>
        /// Загружает ранее сохранённые (не доставленные) результаты при старте приложения,
        /// чтобы они пережили перезапуск.
        /// </summary>
        /// <param name="cancellationToken">Токен отмены операции запуска.</param>
        Task LoadAsync(CancellationToken cancellationToken);
    }
}
