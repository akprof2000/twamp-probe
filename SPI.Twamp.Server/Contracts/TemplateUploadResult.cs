// Ignore Spelling: SPI Twamp

namespace SPI.Twamp.Server.Contracts
{
    /// <summary>
    /// Итог загрузки набора шаблонов.
    /// <para>
    /// Загрузка «всё или ничего»: при непустом <see cref="Errors"/> набор в базе
    /// не изменился, и <see cref="Loaded"/> равно нулю. Так оператор не получает
    /// наполовину применённый набор, по которому часть задач молча не работает.
    /// </para>
    /// </summary>
    public class TemplateUploadResult
    {
        /// <summary>Сколько шаблонов загружено (0, если файл отклонён).</summary>
        public int Loaded { get; set; }

        /// <summary>Что именно неверно в файле — с номерами строк CSV.</summary>
        public IReadOnlyList<string> Errors { get; set; } = [];

        /// <summary>Файл принят: ошибок нет.</summary>
        public bool Ok => Errors.Count == 0;
    }
}
