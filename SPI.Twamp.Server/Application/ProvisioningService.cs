// Ignore Spelling: SPI Twamp

using CsvHelper;
using CsvHelper.Configuration;
using NLog;
using SPI.Twamp.Server.Abstractions;
using SPI.Twamp.Server.Contracts;
using SPI.Twamp.Server.Parser;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SPI.Twamp.Server.Application
{
    /// <summary>
    /// Реализация <see cref="IProvisioningService"/>: загрузка шаблонов и массовая
    /// генерация задач наложением шаблонов на файл маршрутизаторов.
    /// <para>
    /// Из каждой строки файла маршрутизаторов берётся только первое поле вида
    /// «ИМЯ|IP:адрес» — имя устройства и IP (это цель зондирования, EndNode).
    /// Адрес пробы берётся из шаблона. Число задач = маршрутизаторы × шаблоны,
    /// имя задачи — «устройство-шаблон».
    /// </para>
    /// </summary>
    public sealed partial class ProvisioningService(
        Logger logger, ITemplateRepository templates) : IProvisioningService
    {
        private readonly Logger _logger = logger;
        private readonly ITemplateRepository _templates = templates;

        /// <summary>Первое поле строки маршрутизатора: «ИМЯ|IP:x.x.x.x».</summary>
        [GeneratedRegex(@"^\s*([^|\s]+)\|IP:(\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3})")]
        private static partial Regex RouterLineRegex();

        /// <summary>
        /// Разбирает строку файла маршрутизаторов: имя устройства и IP из первого поля
        /// «ИМЯ|IP:адрес». Остальные колонки строки игнорируются.
        /// </summary>
        /// <param name="line">Строка файла.</param>
        /// <param name="name">Имя устройства.</param>
        /// <param name="ip">IP-адрес (цель зондирования).</param>
        /// <returns><c>true</c>, если строка распознана.</returns>
        public static bool TryParseRouterLine(string line, out string name, out string ip)
        {
            Match m = RouterLineRegex().Match(line);
            name = m.Success ? m.Groups[1].Value : "";
            ip = m.Success ? m.Groups[2].Value : "";
            return m.Success;
        }

        /// <summary>
        /// Детерминированный идентификатор задачи из связки «проба + узел + шаблон + устройство».
        /// Повторная загрузка того же файла обновляет существующие задачи, а не создаёт дубли.
        /// </summary>
        public static Guid DeterministicTaskId(string probe, string ip, string templateName, string deviceName)
        {
            byte[] hash = System.Security.Cryptography.MD5.HashData(
                Encoding.UTF8.GetBytes($"{probe}|{ip}|{templateName}|{deviceName}"));
            return new Guid(hash);
        }

        /// <inheritdoc/>
        public async Task<int> UploadTemplatesAsync(Stream csv, CancellationToken cancellationToken)
        {
            using StreamReader reader = new(csv, Encoding.UTF8);

            CsvConfiguration config = new(CultureInfo.InvariantCulture)
            {
                Delimiter = ";",
                // Маппинг по имени заголовка без учёта регистра; лишние колонки игнорируем.
                PrepareHeaderForMatch = args => args.Header.Trim().ToLower(),
                MissingFieldFound = null,
                HeaderValidated = null,
                TrimOptions = TrimOptions.Trim
            };

            using CsvReader csvReader = new(reader, config);
            ProbeTemplate[] parsed = await csvReader.GetRecordsAsync<ProbeTemplate>(cancellationToken)
                .ToArrayAsync(cancellationToken);

            // Шаблон без адреса пробы бесполезен — отбрасываем.
            ProbeTemplate[] valid = [.. parsed.Where(t => !string.IsNullOrWhiteSpace(t.Probe))];

            await _templates.ReplaceAllAsync(valid);
            _logger.Info("Загружено шаблонов: {Count} (отброшено без адреса пробы: {Bad})",
                valid.Length, parsed.Length - valid.Length);
            return valid.Length;
        }

        /// <inheritdoc/>
        public Task<IReadOnlyList<ProbeTemplate>> GetTemplatesAsync() => _templates.GetAllAsync();

        /// <inheritdoc/>
        public async Task<ProvisioningResult> GenerateAsync(Stream routersFile, CancellationToken cancellationToken)
        {
            IReadOnlyList<ProbeTemplate> templates = await _templates.GetAllAsync();
            List<string> rejected = [];

            if (templates.Count == 0)
            {
                rejected.Add("Шаблоны не загружены — сначала вызовите UploadTemplates");
                return new ProvisioningResult([], 0, 0, rejected);
            }

            // --- Разбор файла маршрутизаторов: имя и IP из первого поля «ИМЯ|IP:адрес» ---
            // Файл может быть с заголовком (SNODE CELL_TYPE … RNUM) или без него.
            List<(string Name, string Ip)> routers = [];
            HashSet<string> seen = []; // защита от дублей в файле
            using (StreamReader reader = new(routersFile, Encoding.UTF8))
            {
                string? line;
                int lineNo = 0;
                bool contentSeen = false; // встречали ли уже строку с данными
                while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
                {
                    lineNo++;
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    if (!TryParseRouterLine(line, out string name, out string ip))
                    {
                        // Строку заголовка пропускаем молча: это либо первая строка без
                        // конструкции «|IP:», либо строка с названием колонки SNODE.
                        bool looksLikeHeader = (!contentSeen && !line.Contains("|IP:")) ||
                                               line.Contains("SNODE", StringComparison.OrdinalIgnoreCase);
                        if (looksLikeHeader)
                        {
                            _logger.Debug("Строка {Line} распознана как заголовок и пропущена", lineNo);
                            continue;
                        }

                        rejected.Add($"Строка {lineNo}: не удалось разобрать «ИМЯ|IP:адрес»");
                        continue;
                    }

                    contentSeen = true;
                    if (seen.Add($"{name}|{ip}"))
                    {
                        routers.Add((name, ip));
                    }
                }
            }

            // --- Наложение шаблонов: задач = маршрутизаторы × шаблоны ---
            DateTime createdAt = DateTime.Now;
            List<TaskInfo> tasks = [];

            foreach (ProbeTemplate template in templates)
            {
                // Start/End шаблона: дата или длительность от момента создания.
                DateTime start = TimeSpec.Resolve(template.Start, createdAt, createdAt, out string? startError);
                DateTime end = TimeSpec.Resolve(template.End, createdAt, createdAt.AddDays(14), out string? endError);

                if (startError is not null || endError is not null)
                {
                    rejected.Add($"Шаблон «{template.Name}»: {startError ?? endError}");
                    continue;
                }

                foreach ((string name, string ip) in routers)
                {
                    tasks.Add(new TaskInfo
                    {
                        // Детерминированный Id: повторная загрузка того же файла
                        // обновляет задачи, а не создаёт дубликаты.
                        Id = DeterministicTaskId(template.Probe, ip, template.Name, name),
                        // IP включён в имя, чтобы задачи различались даже когда одно
                        // устройство встречается в файле с несколькими адресами.
                        Title = $"{name}-{ip}-{template.Name}",
                        RequestInfo = template.Probe,
                        EndNode = ip,
                        Type = template.Type,
                        Mode = template.Mode,
                        CronExpression = template.Cron,
                        CronWithSeconds = false,
                        ContinueIfError = true,
                        Repeats = template.Repeats,
                        Circles = template.Circles,
                        PauseSec = template.Pause,
                        TimeoutSec = template.Timeout,
                        Create = createdAt,
                        Start = start,
                        End = end,
                        Delete = false,
                        Parameters = string.IsNullOrWhiteSpace(template.Request)
                            ? []
                            : new Dictionary<string, string> { { "all", template.Request.Trim() } }
                    });
                }
            }

            _logger.Info("Сгенерировано задач: {Tasks} (маршрутизаторов {Routers} × шаблонов {Templates})",
                tasks.Count, routers.Count, templates.Count);

            return new ProvisioningResult(tasks, routers.Count, templates.Count, rejected);
        }

        /// <inheritdoc/>
        public byte[] BuildCsv(IReadOnlyList<TaskInfo> tasks)
        {
            const char sep = ';';
            StringBuilder sb = new();

            // Заголовок совместим с форматом «Base test.csv» (его понимает UploadCsv).
            _ = sb.AppendLine(string.Join(sep,
                ["Name", "HostName", "Ip", "Probe", "Request", "Type", "Repeats", "Circles",
                 "Pause", "Cron", "Start", "End", "Mode", "Timeout"]));

            foreach (TaskInfo t in tasks)
            {
                string request = t.Parameters.TryGetValue("all", out string? args) ? args : "";
                _ = sb.AppendLine(string.Join(sep, new[]
                {
                    TwPingParser.CsvEscape(t.Title),
                    "", // HostName не используется
                    TwPingParser.CsvEscape(t.EndNode),
                    TwPingParser.CsvEscape(t.RequestInfo),
                    TwPingParser.CsvEscape(request),
                    t.Type.ToString(),
                    t.Repeats.ToString(),
                    t.Circles.ToString(),
                    t.PauseSec.ToString(),
                    TwPingParser.CsvEscape(t.CronExpression),
                    t.Start.ToString("dd.MM.yyyy HH:mm"),
                    t.End.ToString("dd.MM.yyyy HH:mm"),
                    t.Mode.ToString(),
                    t.TimeoutSec.ToString()
                }));
            }

            return Encoding.UTF8.GetBytes(sb.ToString());
        }

    }
}
