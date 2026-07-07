// Ignore Spelling: SPI Twamp

using NLog;
using spi.twamp.Probe.Environment;
using SPI.Twamp.Probe.Abstractions;
using SPI.Twamp.Probe.Contracts;
using System.Diagnostics;
using System.Text;

namespace SPI.Twamp.Probe.Runners
{
    /// <summary>
    /// Выполняет внешний зонд (системный ping или утилита TWamp) для узлов задачи.
    /// <para>
    /// Вся работа с процессом асинхронная: ожидание завершения и чтение вывода
    /// не блокируют поток пула.
    /// </para>
    /// <para>
    /// Предел одновременных запусков задаётся не здесь, а числом воркеров
    /// <see cref="Server.ProbeDispatcher"/> — исполнитель просто честно выполняет то,
    /// что ему передал диспетчер.
    /// </para>
    /// </summary>
    public sealed class ProbeRunner(Logger logger, IConfiguration configuration, IResultStore resultStore) : IProbeRunner
    {
        /// <summary>Разделители списка узлов в поле <see cref="TaskInfo.EndNode"/>.</summary>
        private static readonly char[] NodeSeparators = [';', ','];

        private readonly Logger _logger = logger;
        private readonly IConfiguration _configuration = configuration;
        private readonly IResultStore _resultStore = resultStore;

        /// <inheritdoc/>
        public async Task RunForNodesAsync(TaskInfo task, CancellationToken cancellationToken)
        {
            string[] nodes = task.EndNode.Split(
                NodeSeparators,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            // Узлы обрабатываем параллельно — каждый в своей асинхронной цепочке.
            IEnumerable<Task> probes = nodes.Select(node => RunSingleNodeAsync(task, node, cancellationToken));
            await Task.WhenAll(probes);
        }

        /// <summary>Выполняет все циклы и повторы зонда для одного узла.</summary>
        private async Task RunSingleNodeAsync(TaskInfo task, string node, CancellationToken cancellationToken)
        {
            (string? execute, string arguments) = BuildCommand(task, node);
            // Debug, а не Info: на пике из тысяч зондов подробные записи не должны нагружать логгер.
            _logger.Debug("Запуск зонда {Execute} с аргументами {Arguments}", execute, arguments);

            // Circles — количество полных циклов замера, Repeats — число запусков внутри цикла.
            for (int circle = 0; circle < task.Circles; circle++)
            {
                for (int repeat = 0; repeat < task.Repeats; repeat++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    ActionData result = await ExecuteOnceAsync(task, node, execute, arguments, cancellationToken);
                    // Результат сразу попадает в хранилище и становится доступен веб-интерфейсу.
                    _resultStore.Add(result);
                }

                // Пауза между циклами (кроме последнего) — без блокировки потока.
                bool isLastCircle = circle == task.Circles - 1;
                if (!isLastCircle && task.PauseSec > 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(task.PauseSec), cancellationToken);
                }
            }
        }

        /// <summary>Формирует имя исполняемого файла и строку аргументов в зависимости от режима задачи.</summary>
        private (string? execute, string arguments) BuildCommand(TaskInfo task, string node)
        {
            StringBuilder arguments = new();
            string? execute = null;

            switch (task.Mode)
            {
                case TaskMode.WinPing:
                    // Для ping адрес узла идёт первым аргументом, затем параметры.
                    execute = _configuration["ping:name"];
                    _ = arguments.Append(node);
                    AppendParameters(arguments, task, _configuration["ping:default"]);
                    break;

                case TaskMode.TWamp:
                    // Для TWamp сначала параметры, адрес узла — последним аргументом.
                    execute = _configuration["twamp:name"];
                    AppendParameters(arguments, task, _configuration["twamp:default"]);
                    _ = arguments.Append(' ').Append(node);
                    break;
            }

            return (execute, arguments.ToString());
        }

        /// <summary>Добавляет параметры задачи либо значение по умолчанию из конфигурации.</summary>
        private static void AppendParameters(StringBuilder arguments, TaskInfo task, string? defaultValue)
        {
            if (task.Parameters.Count > 0)
            {
                foreach (string value in task.Parameters.Values)
                {
                    _ = arguments.Append(' ').Append(value);
                }
            }
            else
            {
                _ = arguments.Append(' ').Append(defaultValue);
            }
        }

        /// <summary>Запускает дочерний процесс один раз и возвращает собранный результат замера.</summary>
        private async Task<ActionData> ExecuteOnceAsync(
            TaskInfo task, string node, string? execute, string arguments, CancellationToken cancellationToken)
        {
            using Process process = new()
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = execute,                 // исполняемый файл зонда
                    Arguments = arguments,              // аргументы командной строки
                    UseShellExecute = false,            // запуск без системной оболочки
                    RedirectStandardOutput = true,      // перехватываем стандартный вывод
                    RedirectStandardError = true,       // перехватываем поток ошибок
                    RedirectStandardInput = true,
                    CreateNoWindow = true,              // без создания окна
                    WindowStyle = ProcessWindowStyle.Hidden
                }
            };

            _ = process.Start();

            // Читаем stdout и stderr одновременно, чтобы избежать взаимоблокировки
            // при переполнении буфера одного из потоков вывода.
            Task<string> outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

            // Индивидуальный таймаут задачи: связанный токен отменяется либо при
            // остановке сервиса (cancellationToken), либо по истечении времени задачи.
            bool timedOut = false;
            using CancellationTokenSource timeoutCts =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            TimeSpan timeout = GetTimeout(task);
            if (timeout > TimeSpan.Zero)
            {
                timeoutCts.CancelAfter(timeout);
            }

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Отменил не сервис, а собственный таймаут задачи — завершаем процесс силой.
                timedOut = true;
                KillProcess(process, task, node, timeout);
            }

            // После Kill потоки вывода закрываются, поэтому чтение корректно завершится
            // и вернёт то, что процесс успел напечатать до принудительного завершения.
            string output = await outputTask;
            string error = await errorTask;

            if (timedOut)
            {
                string note = $"Задача прервана по таймауту {timeout.TotalSeconds:0.###} c и принудительно завершена.";
                error = string.IsNullOrEmpty(error) ? note : $"{error}{System.Environment.NewLine}{note}";
            }

            _logger.Debug("Зонд для узла {Node} завершён. Вывод: {Output}", node, output);
            if (!string.IsNullOrEmpty(error))
            {
                _logger.Warn("Зонд для узла {Node} вернул ошибку: {Error}", node, error);
            }

            return new ActionData
            {
                Console = output,
                ErrorConsole = error,
                EndNode = node,
                IPAddress = task.IpAddress,
                TaskId = task.Id,
                RequestInfo = task.RequestInfo
            };
        }

        /// <summary>Возвращает индивидуальный таймаут задачи или бесконечность, если он не задан.</summary>
        private static TimeSpan GetTimeout(TaskInfo task) =>
            task.TimeoutSec > 0 ? TimeSpan.FromSeconds(task.TimeoutSec) : Timeout.InfiniteTimeSpan;

        /// <summary>Принудительно завершает процесс зонда (вместе с дочерними) по таймауту.</summary>
        private void KillProcess(Process process, TaskInfo task, string node, TimeSpan timeout)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
                _logger.Warn(
                    "Задача {Guid}: зонд для узла {Node} превысил таймаут {Timeout} c и принудительно завершён",
                    task.Id, node, timeout.TotalSeconds);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Не удалось принудительно завершить процесс зонда для узла {Node}", node);
            }
        }
    }
}
