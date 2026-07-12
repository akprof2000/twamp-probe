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

        /// <summary>Проверка, что строка — корректный IPv4-адрес.</summary>
        [GeneratedRegex(@"^\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}$")]
        private static partial Regex IpRegex();

        /// <summary>
        /// Разбирает файл маршрутизаторов. Поддерживаются форматы:
        /// <list type="bullet">
        /// <item>CSV с разделителем «;» и заголовками (колонки SNODE и IP находятся по именам);</item>
        /// <item>выгрузка с табуляцией или пробелами, с заголовком или без.</item>
        /// </list>
        /// Имя и IP берутся из поля SNODE вида «ИМЯ|IP:адрес»; если в SNODE нет
        /// конструкции «|IP:», именем считается само значение SNODE, а адрес берётся
        /// из отдельной колонки IP. Дубликаты (имя+IP) отбрасываются.
        /// </summary>
        /// <param name="lines">Строки файла.</param>
        /// <returns>Список маршрутизаторов и список нераспознанных строк.</returns>
        public static (IReadOnlyList<(string Name, string Ip)> Routers, IReadOnlyList<string> Rejected)
            ParseRouterFile(IEnumerable<string> lines)
        {
            List<(string Name, string Ip)> routers = [];
            List<string> rejected = [];
            HashSet<string> seen = [];      // защита от дублей в файле

            char? separator = null;         // ';' или '\t' — определяется по первой строке
            int snodeIndex = 0;             // колонка SNODE (по умолчанию — первая)
            int ipIndex = -1;               // колонка IP (если найдена в заголовке)
            bool contentSeen = false;
            int lineNo = 0;

            foreach (string line in lines)
            {
                lineNo++;
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                // Строка заголовка: определяем разделитель и позиции колонок SNODE/IP.
                if (!contentSeen && line.Contains("SNODE", StringComparison.OrdinalIgnoreCase))
                {
                    ReadHeader(line, ref separator, ref snodeIndex, ref ipIndex);
                    continue;
                }

                // Разделитель для файлов без заголовка — по первой строке данных.
                separator ??= line.Contains(';') ? ';' : '\t';

                string[] fields = line.Split(separator.Value);
                string snode = snodeIndex < fields.Length ? fields[snodeIndex].Trim() : "";

                if (!TryResolveRouter(snode, fields, ipIndex, out string name, out string ip))
                {
                    // Первую непохожую на данные строку молча считаем заголовком.
                    if (!contentSeen && !line.Contains("|IP:"))
                    {
                        continue;
                    }
                    rejected.Add($"Строка {lineNo}: не удалось определить имя и IP маршрутизатора");
                    continue;
                }

                contentSeen = true;
                if (seen.Add($"{name}|{ip}"))
                {
                    routers.Add((name, ip));
                }
            }

            return (routers, rejected);
        }

        /// <summary>Разбирает строку заголовка: определяет разделитель и позиции колонок SNODE/IP.</summary>
        private static void ReadHeader(string line, ref char? separator, ref int snodeIndex, ref int ipIndex)
        {
            separator = line.Contains(';') ? ';' : '\t';
            string[] header = line.Split(separator.Value);
            for (int i = 0; i < header.Length; i++)
            {
                string column = header[i].Trim().ToUpperInvariant();
                if (column == "SNODE")
                {
                    snodeIndex = i;
                }
                else if (column == "IP")
                {
                    ipIndex = i;
                }
            }
        }

        /// <summary>
        /// Определяет имя и IP маршрутизатора: из поля SNODE вида «ИМЯ|IP:адрес» либо
        /// из имени SNODE и отдельной колонки IP. Возвращает <c>false</c>, если не удалось.
        /// </summary>
        private static bool TryResolveRouter(string snode, string[] fields, int ipIndex, out string name, out string ip)
        {
            // Классический вид «ИМЯ|IP:адрес» — всё в одном поле.
            if (TryParseRouterLine(snode, out name, out ip))
            {
                return true;
            }

            // SNODE без «|IP:» — имя из SNODE, адрес из колонки IP.
            if (snode.Length > 0 && ipIndex >= 0 && ipIndex < fields.Length &&
                IpRegex().IsMatch(fields[ipIndex].Trim()))
            {
                name = snode;
                ip = fields[ipIndex].Trim();
                return true;
            }

            return false;
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
        public async Task<int> UploadTemplatesAsync(Stream csv, string setName, CancellationToken cancellationToken)
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

            await _templates.ReplaceSetAsync(setName, valid);
            _logger.Info("Набор шаблонов «{Set}»: загружено {Count} (отброшено без адреса пробы: {Bad})",
                setName, valid.Length, parsed.Length - valid.Length);
            return valid.Length;
        }

        /// <inheritdoc/>
        public Task<IReadOnlyList<ProbeTemplate>> GetTemplatesAsync() => _templates.GetAllAsync();

        /// <inheritdoc/>
        public Task<IReadOnlyList<(string SetName, int Count)>> GetTemplateSetsAsync() => _templates.GetSetsAsync();

        /// <inheritdoc/>
        public async Task<int> DeleteTemplateSetAsync(string setName)
        {
            int removed = await _templates.DeleteSetAsync(setName);
            _logger.Info("Набор шаблонов «{Set}» удалён ({Count} шаблонов)", setName, removed);
            return removed;
        }

        /// <inheritdoc/>
        public async Task<ProvisioningResult> GenerateAsync(Stream routersFile, string? setName, CancellationToken cancellationToken)
        {
            // Применяем конкретный набор шаблонов, либо все наборы, если имя не задано.
            IReadOnlyList<ProbeTemplate> activeTemplates = string.IsNullOrWhiteSpace(setName)
                ? await _templates.GetAllAsync()
                : await _templates.GetBySetAsync(setName);
            List<string> rejected = [];

            if (activeTemplates.Count == 0)
            {
                rejected.Add(string.IsNullOrWhiteSpace(setName)
                    ? "Шаблоны не загружены — сначала вызовите UploadTemplates"
                    : $"Набор шаблонов «{setName}» не найден или пуст");
                return new ProvisioningResult([], 0, 0, rejected);
            }

            // --- Разбор файла маршрутизаторов (CSV «;», табуляция или пробелы) ---
            List<string> lines = [];
            using (StreamReader reader = new(routersFile, Encoding.UTF8))
            {
                string? line;
                while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
                {
                    lines.Add(line);
                }
            }

            (IReadOnlyList<(string Name, string Ip)> routers, IReadOnlyList<string> parseErrors) =
                ParseRouterFile(lines);
            rejected.AddRange(parseErrors);

            // --- Наложение шаблонов: задач = маршрутизаторы × шаблоны ---
            DateTime createdAt = DateTime.Now;
            List<TaskInfo> tasks = [];

            foreach (ProbeTemplate template in activeTemplates)
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
                        // обновляет задачи, а не создаёт дубликаты. Имя набора включено,
                        // чтобы одноимённые шаблоны из разных наборов не пересекались.
                        Id = DeterministicTaskId(template.Probe, ip, $"{template.SetName}/{template.Name}", name),
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
                tasks.Count, routers.Count, activeTemplates.Count);

            return new ProvisioningResult(tasks, routers.Count, activeTemplates.Count, rejected);
        }

        /// <inheritdoc/>
        public byte[] BuildCsv(IReadOnlyList<TaskInfo> tasks)
        {
            const char sep = ';';
            StringBuilder sb = new();

            // Заголовок совместим с форматом «Base test.csv» (его понимает UploadCsv).
            _ = sb.AppendLine(string.Join(sep,
                "Name", "HostName", "Ip", "Probe", "Request", "Type", "Repeats", "Circles",
                "Pause", "Cron", "Start", "End", "Mode", "Timeout"));

            foreach (TaskInfo t in tasks)
            {
                string request = t.Parameters.TryGetValue("all", out string? args) ? args : "";
                _ = sb.AppendLine(string.Join(sep,
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
                    t.TimeoutSec.ToString()));
            }

            return Encoding.UTF8.GetBytes(sb.ToString());
        }

    }
}
