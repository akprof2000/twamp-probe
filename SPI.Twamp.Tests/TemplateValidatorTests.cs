// Ignore Spelling: SPI Twamp Cron

using SPI.Twamp.Server.Application;
using SPI.Twamp.Server.Contracts;
using Xunit;

namespace SPI.Twamp.Tests
{
    /// <summary>
    /// Проверка шаблонов перед загрузкой. Смысл всех тестов один: ошибка должна
    /// найтись до записи и назвать строку и колонку — иначе неверный шаблон
    /// доезжает до задач и обнаруживается уже на пробе, когда замеры не идут.
    /// </summary>
    public class TemplateValidatorTests
    {
        /// <summary>Заведомо корректный шаблон — основа для порчи одного поля.</summary>
        private static ProbeTemplate Good() => new()
        {
            Name = "d46",
            Probe = "http://10.0.0.5:8443",
            Request = "-c 300 -i 1",
            Type = TaskType.Scheduler,
            Repeats = 1,
            Circles = 1,
            Pause = 0,
            Cron = "*/5 * * * *",
            Mode = TaskMode.TWamp,
            Timeout = 10
        };

        [Fact(DisplayName = "Корректный набор принимается без замечаний")]
        public void Valid_Passes()
        {
            IReadOnlyList<string> errors = TemplateValidator.Validate([Good(), Good()]);
            Assert.Empty(errors);
        }

        [Fact(DisplayName = "Пустой файл отклоняется с объяснением")]
        public void Empty_Rejected()
        {
            IReadOnlyList<string> errors = TemplateValidator.Validate([]);

            string message = Assert.Single(errors);
            Assert.Contains("не содержит", message);
        }

        [Fact(DisplayName = "Шаблон без адреса пробы отклоняется, а не отбрасывается молча")]
        public void MissingProbe_Rejected()
        {
            ProbeTemplate bad = Good();
            bad.Probe = "";

            IReadOnlyList<string> errors = TemplateValidator.Validate([bad]);

            string message = Assert.Single(errors);
            Assert.Contains("Probe", message);
            Assert.Contains("Строка 2", message); // первая строка данных идёт после заголовка
        }

        [Theory(DisplayName = "Адрес пробы должен быть http(s)-адресом")]
        [InlineData("10.0.0.5")]
        [InlineData("10.0.0.5:8443")]
        [InlineData("ftp://10.0.0.5")]
        [InlineData("просто текст")]
        public void BadProbeAddress_Rejected(string probe)
        {
            ProbeTemplate bad = Good();
            bad.Probe = probe;

            IReadOnlyList<string> errors = TemplateValidator.Validate([bad]);

            string message = Assert.Single(errors);
            Assert.Contains("Probe", message);
            Assert.Contains("http://", message); // подсказка, как надо
        }

        [Theory(DisplayName = "Некорректное расписание отклоняется")]
        [InlineData("каждые пять минут")]
        [InlineData("*/5 * *")]          // мало полей
        [InlineData("*/5 * * * * *")]    // шесть полей: задачи из шаблонов пятипольные
        [InlineData("99 * * * *")]       // минута вне диапазона
        [InlineData("* * * * 9")]        // день недели вне диапазона
        public void BadCron_Rejected(string cron)
        {
            ProbeTemplate bad = Good();
            bad.Cron = cron;

            IReadOnlyList<string> errors = TemplateValidator.Validate([bad]);

            string message = Assert.Single(errors);
            Assert.Contains("Cron", message);
        }

        [Theory(DisplayName = "Рабочие расписания принимаются")]
        [InlineData("*/5 * * * *")]
        [InlineData("0 3 * * *")]
        [InlineData("15,45 * * * 1-5")]
        [InlineData("0 0 1 1 *")]
        public void GoodCron_Passes(string cron)
        {
            ProbeTemplate good = Good();
            good.Cron = cron;

            Assert.Empty(TemplateValidator.Validate([good]));
        }

        [Fact(DisplayName = "Нулевые повторы и циклы отклоняются: замер не выполнится")]
        public void ZeroCounts_Rejected()
        {
            ProbeTemplate bad = Good();
            bad.Repeats = 0;
            bad.Circles = 0;

            IReadOnlyList<string> errors = TemplateValidator.Validate([bad]);

            Assert.Equal(2, errors.Count);
            Assert.Contains(errors, e => e.Contains("Repeats"));
            Assert.Contains(errors, e => e.Contains("Circles"));
        }

        [Fact(DisplayName = "Отрицательный таймаут отклоняется")]
        public void NegativeTimeout_Rejected()
        {
            ProbeTemplate bad = Good();
            bad.Timeout = -5;

            string message = Assert.Single(TemplateValidator.Validate([bad]));
            Assert.Contains("Timeout", message);
        }

        [Fact(DisplayName = "Неразбираемая длительность в Start отклоняется")]
        public void BadStart_Rejected()
        {
            ProbeTemplate bad = Good();
            // Ни даты, ни единиц времени: «недельку» здесь не подойдёт — TimeSpec
            // намеренно терпим к формам слов и распознал бы её как неделю.
            bad.Start = "как получится";

            string message = Assert.Single(TemplateValidator.Validate([bad]));
            Assert.Contains("Start", message);
        }

        [Theory(DisplayName = "Формы записи времени, которые TimeSpec понимает, проходят")]
        [InlineData("2 week 3 day")]
        [InlineData("1 год 2 месяца")]
        [InlineData("2д")]
        [InlineData("01.03.2026 10:00")]
        public void GoodStart_Passes(string start)
        {
            ProbeTemplate good = Good();
            good.Start = start;
            good.End = "10 year"; // заведомо позже начала

            Assert.Empty(TemplateValidator.Validate([good]));
        }

        [Fact(DisplayName = "Окончание раньше начала отклоняется")]
        public void EndBeforeStart_Rejected()
        {
            ProbeTemplate bad = Good();
            bad.Start = "2 day";
            bad.End = "1 hour";

            string message = Assert.Single(TemplateValidator.Validate([bad]));
            Assert.Contains("не позже начала", message);
        }

        [Fact(DisplayName = "Ошибка называет номер строки CSV")]
        public void ErrorPointsToLine()
        {
            ProbeTemplate bad = Good();
            bad.Cron = "мусор";

            // Три корректных шаблона, четвёртый испорчен: в файле это строка 5.
            IReadOnlyList<string> errors = TemplateValidator.Validate([Good(), Good(), Good(), bad]);

            string message = Assert.Single(errors);
            Assert.Contains("Строка 5", message);
        }

        [Fact(DisplayName = "Длинный список ошибок обрезается, но об этом сказано")]
        public void TooManyErrors_Truncated()
        {
            ProbeTemplate bad = Good();
            bad.Probe = "";

            List<ProbeTemplate> many = [];
            for (int i = 0; i < 200; i++)
            {
                many.Add(bad);
            }

            IReadOnlyList<string> errors = TemplateValidator.Validate(many);

            Assert.True(errors.Count < 200, "список ошибок должен обрезаться");
            Assert.Contains(errors, e => e.Contains("показаны первые"));
        }
    }
}
