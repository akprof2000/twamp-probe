// Ignore Spelling: SPI Twamp

using LiteDB.Async;
using SPI.Twamp.Server.Abstractions;
using SPI.Twamp.Server.Contracts;

namespace SPI.Twamp.Server.Infrastructure
{
    /// <summary>
    /// Реализация <see cref="ITaskRepository"/> поверх LiteDB.
    /// </summary>
    public sealed class TaskRepository(LiteDbContext context) : ITaskRepository
    {
        private readonly LiteDbContext _context = context;

        /// <inheritdoc/>
        public async Task UpsertAsync(TaskInfo task) => _ = await _context.Tasks.UpsertAsync(task);

        /// <inheritdoc/>
        public async Task UpsertRangeAsync(IEnumerable<TaskInfo> tasks) =>
            _ = await _context.Tasks.UpsertAsync(tasks);

        /// <inheritdoc/>
        public async Task<TaskInfo?> GetByIdAsync(Guid id) =>
            await _context.Tasks.FindOneAsync(x => x.Id == id);

        /// <inheritdoc/>
        public async Task<IReadOnlyList<TaskInfo>> GetByRequestInfoAsync(string requestInfo)
        {
            IEnumerable<TaskInfo> data = await _context.Tasks.FindAsync(x => x.RequestInfo == requestInfo);
            return [.. data];
        }

        /// <inheritdoc/>
        public async Task<IReadOnlyList<TaskInfo>> GetAllAsync()
        {
            IEnumerable<TaskInfo> data = await _context.Tasks.FindAllAsync();
            return [.. data];
        }

        /// <inheritdoc/>
        public async Task MarkDeletedByRequestInfoAsync(string requestInfo)
        {
            // Помечаем все задачи пробы удалёнными и сохраняем — так проба на следующей
            // синхронизации остановит их выполнение.
            IEnumerable<TaskInfo> data = await _context.Tasks.FindAsync(x => x.RequestInfo == requestInfo);
            foreach (TaskInfo task in data)
            {
                task.Delete = true;
                task.DeletedAt ??= DateTime.Now;
                _ = await _context.Tasks.UpsertAsync(task);
            }
        }

        /// <inheritdoc/>
        public async Task RemoveAsync(Guid id) => _ = await _context.Tasks.DeleteAsync(id);

        /// <inheritdoc/>
        public async Task<IReadOnlyList<Guid>> RemoveByRequestInfoAsync(string requestInfo)
        {
            IEnumerable<TaskInfo> doomed = await _context.Tasks.FindAsync(x => x.RequestInfo == requestInfo);
            List<Guid> ids = [.. doomed.Select(t => t.Id)];
            _ = await _context.Tasks.DeleteManyAsync(x => x.RequestInfo == requestInfo);
            return ids;
        }
    }
}
