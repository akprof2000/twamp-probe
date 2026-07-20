// Ignore Spelling: SPI Twamp

using LiteDB;
using LiteDB.Async;
using SPI.Twamp.Server.Contracts;

namespace SPI.Twamp.Server.Infrastructure
{
    /// <summary>
    /// Контекст базы данных LiteDB: единственный владелец подключения к файлу БД.
    /// Предоставляет типизированные коллекции репозиториям, скрывая имена коллекций
    /// и строку подключения в одном месте.
    /// </summary>
    public sealed class LiteDbContext : IDisposable
    {
        private readonly LiteDatabaseAsync _db;

        /// <summary>Открывает (или создаёт) файл БД по пути из конфигурации («Database:Path»).</summary>
        public LiteDbContext(IConfiguration configuration)
        {
            string path = configuration["Database:Path"] ?? "TWamp.db";
            _db = new LiteDatabaseAsync(new ConnectionString(path));
        }

        /// <summary>Коллекция задач.</summary>
        public ILiteCollectionAsync<TaskInfo> Tasks => _db.GetCollection<TaskInfo>("tasksinfo");

        /// <summary>Коллекция подтверждённых проб (клиентов).</summary>
        public ILiteCollectionAsync<Client> Clients => _db.GetCollection<Client>("clients");

        /// <summary>Коллекция неопознанных проб, ожидающих подтверждения.</summary>
        public ILiteCollectionAsync<Identify> Identify => _db.GetCollection<Identify>("identify");

        /// <summary>Коллекция результатов зондирования.</summary>
        public ILiteCollectionAsync<ActionData> Actions => _db.GetCollection<ActionData>("actiondata");

        /// <summary>Коллекция шаблонов задач.</summary>
        public ILiteCollectionAsync<ProbeTemplate> Templates => _db.GetCollection<ProbeTemplate>("templates");

        /// <summary>Коллекция разобранной статистики замеров.</summary>
        public ILiteCollectionAsync<StatRecord> Stats => _db.GetCollection<StatRecord>("stats");

        /// <summary>Коллекция отложенных очисток удалённых проб.</summary>
        public ILiteCollectionAsync<PendingProbeCleanup> Cleanups =>
            _db.GetCollection<PendingProbeCleanup>("probe_cleanups");

        /// <summary>
        /// Переносит накопленные изменения из WAL-журнала (файл «*-log.db») в основную
        /// базу и очищает журнал. Без периодического вызова журнал растёт бесконечно
        /// при интенсивной записи результатов.
        /// </summary>
        public Task CheckpointAsync() => _db.CheckpointAsync();

        /// <summary>Закрывает подключение к БД.</summary>
        public void Dispose() => _db.Dispose();
    }
}
