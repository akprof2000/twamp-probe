// Ignore Spelling: SPI Twamp

using SPI.Twamp.Probe.Contracts;

namespace SPI.Twamp.Probe.Abstractions
{
    /// <summary>
    /// Исполнитель зонда: запускает внешнюю утилиту (системный ping или TWamp)
    /// для узлов задачи и складывает результаты в <see cref="IResultStore"/>.
    /// <para>
    /// Вся работа с процессом асинхронная — поток пула не блокируется на время
    /// ожидания завершения дочернего процесса или пауз между циклами.
    /// </para>
    /// </summary>
    public interface IProbeRunner
    {
        /// <summary>
        /// Асинхронно выполняет зонд для всех узлов задачи.
        /// Список узлов берётся из <see cref="TaskInfo.EndNode"/> (разделители «;» и «,»)
        /// и обрабатывается параллельно.
        /// </summary>
        /// <param name="task">Описание задачи с параметрами запуска.</param>
        /// <param name="cancellationToken">Токен отмены выполнения.</param>
        Task RunForNodesAsync(TaskInfo task, CancellationToken cancellationToken);
    }
}
