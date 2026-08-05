// Ignore Spelling: SPI Twamp Cron

using NCrontab;
using SPI.Twamp.Server.Contracts;

namespace SPI.Twamp.Server.Application
{
    /// <summary>
    /// Проверка шаблонов задач перед загрузкой.
    /// <para>
    /// Раньше файл принимался почти без разбора: строки без адреса пробы молча
    /// отбрасывались, а всё остальное — неверный cron, несуществующий режим,
    /// нулевое число повторов — доезжало до задач и обнаруживалось уже на пробе,
    /// когда замеры не шли. Поэтому проверка идёт до записи: набор либо
    /// загружается целиком, либо не загружается вовсе, а оператор получает
    /// список того, что именно исправить и в какой строке.
    /// </para>
    /// </summary>
    public static class TemplateValidator
    {
        /// <summary>Сколько ошибок показывать: дальше список перестаёт быть полезным.</summary>
        private const int MaxReportedErrors = 50;

        /// <summary>
        /// Проверяет разобранные шаблоны и возвращает список ошибок.
        /// Пустой список означает, что набор можно загружать.
        /// </summary>
        /// <param name="templates">Шаблоны в том порядке, в каком они шли в файле.</param>
        /// <returns>Ошибки с номерами строк CSV.</returns>
        public static IReadOnlyList<string> Validate(IReadOnlyList<ProbeTemplate> templates)
        {
            List<string> errors = [];

            if (templates.Count == 0)
            {
                errors.Add("Файл не содержит ни одной строки с данными. " +
                    "Ожидается CSV с заголовками и разделителем «;».");
                return errors;
            }

            for (int i = 0; i < templates.Count; i++)
            {
                // Строка 1 — заголовок, поэтому данные начинаются со второй.
                int line = i + 2;
                ValidateOne(templates[i], line, errors);

                if (errors.Count >= MaxReportedErrors)
                {
                    errors.Add($"…и, возможно, другие ошибки — показаны первые {MaxReportedErrors}.");
                    break;
                }
            }

            return errors;
        }

        /// <summary>Проверяет один шаблон, дописывая найденное в общий список.</summary>
        private static void ValidateOne(ProbeTemplate template, int line, List<string> errors)
        {
            // --- Адрес пробы: без него шаблон не к чему применить ---
            if (string.IsNullOrWhiteSpace(template.Probe))
            {
                errors.Add($"Строка {line}: не заполнена колонка «Probe» — " +
                    "укажите адрес пробы, например http://10.0.0.5:8443");
            }
            else if (!Uri.TryCreate(template.Probe.Trim(), UriKind.Absolute, out Uri? probeUri)
                || (probeUri.Scheme != Uri.UriSchemeHttp && probeUri.Scheme != Uri.UriSchemeHttps))
            {
                errors.Add($"Строка {line}: «Probe» = «{template.Probe}» — это не адрес пробы. " +
                    "Ожидается http://хост:порт или https://хост:порт");
            }

            // --- Расписание: неверный cron означает задачу, которая не запустится ---
            if (string.IsNullOrWhiteSpace(template.Cron))
            {
                errors.Add($"Строка {line}: не заполнена колонка «Cron» — " +
                    "укажите расписание, например «*/5 * * * *»");
            }
            else
            {
                // Из шаблонов задачи создаются пятипольными (CronWithSeconds = false),
                // поэтому и проверяем классический синтаксис без секунд.
                if (CrontabSchedule.TryParse(template.Cron.Trim()) is null)
                {
                    errors.Add($"Строка {line}: «Cron» = «{template.Cron}» — некорректное расписание. " +
                        "Ожидается пять полей «минуты часы день месяц день_недели», например «*/5 * * * *»");
                }
            }

            // --- Числовые поля ---
            if (template.Repeats == 0)
            {
                errors.Add($"Строка {line}: «Repeats» = 0 — замер не выполнится ни разу. " +
                    "Укажите 1 или больше");
            }

            if (template.Circles == 0)
            {
                errors.Add($"Строка {line}: «Circles» = 0 — замер не выполнится ни разу. " +
                    "Укажите 1 или больше");
            }

            if (template.Timeout < 0)
            {
                errors.Add($"Строка {line}: «Timeout» = {template.Timeout} — " +
                    "таймаут не может быть отрицательным (0 означает «без ограничения»)");
            }

            // --- Границы действия задачи ---
            DateTime now = DateTime.Now;
            DateTime start = TimeSpec.Resolve(template.Start, now, now, out string? startError);
            if (startError is not null)
            {
                errors.Add($"Строка {line}: «Start» = «{template.Start}» — {startError}");
            }

            DateTime end = TimeSpec.Resolve(template.End, now, now.AddDays(14), out string? endError);
            if (endError is not null)
            {
                errors.Add($"Строка {line}: «End» = «{template.End}» — {endError}");
            }

            if (startError is null && endError is null && end <= start)
            {
                errors.Add($"Строка {line}: окончание «{template.End}» не позже начала «{template.Start}» — " +
                    "задача завершится, не начавшись");
            }
        }
    }
}
