// Ignore Spelling: SPI Twamp

using SPI.Twamp.Probe.Contracts;

namespace SPI.Twamp.Probe.Abstractions
{
    /// <summary>
    /// Потокобезопасное хранилище результатов зондирования.
    /// <para>
    /// Накапливает результаты от параллельных задач и выдаёт их серверу пачками
    /// с подтверждением доставки: пачка удаляется только после <see cref="ConfirmAsync"/>,
    /// а до подтверждения повторно выдаётся при следующем опросе. Ожидание новых
    /// данных асинхронное и не удерживает поток пула.
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
        /// Асинхронно ожидает появления результатов и возвращает пачку на доставку.
        /// Если есть неподтверждённая пачка — возвращает её повторно.
        /// </summary>
        /// <param name="timeout">Максимальное время ожидания новых результатов.</param>
        /// <param name="cancellationToken">Токен отмены (например, разрыв соединения клиентом).</param>
        /// <returns>Пачка результатов (пустая с <see cref="Guid.Empty"/> при таймауте).</returns>
        Task<ResultBatch> TakeBatchAsync(TimeSpan timeout, CancellationToken cancellationToken);

        /// <summary>
        /// Подтверждает доставку пачки: результаты записаны сервером и могут быть удалены.
        /// </summary>
        /// <param name="batchId">Идентификатор подтверждаемой пачки.</param>
        /// <returns><c>true</c>, если пачка найдена и удалена.</returns>
        Task<bool> ConfirmAsync(Guid batchId);

        /// <summary>
        /// Загружает ранее сохранённые (не доставленные) результаты при старте приложения,
        /// чтобы они пережили перезапуск.
        /// </summary>
        /// <param name="cancellationToken">Токен отмены операции запуска.</param>
        Task LoadAsync(CancellationToken cancellationToken);
    }
}
