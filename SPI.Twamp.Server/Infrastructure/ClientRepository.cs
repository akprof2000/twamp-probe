// Ignore Spelling: SPI Twamp

using LiteDB.Async;
using SPI.Twamp.Server.Abstractions;
using SPI.Twamp.Server.Contracts;

namespace SPI.Twamp.Server.Infrastructure
{
    /// <summary>
    /// Реализация <see cref="IClientRepository"/> поверх LiteDB (коллекции clients и identify).
    /// </summary>
    public sealed class ClientRepository(LiteDbContext context) : IClientRepository
    {
        private readonly LiteDbContext _context = context;

        /// <inheritdoc/>
        public async Task<IReadOnlyList<Client>> GetAllAsync()
        {
            IEnumerable<Client> data = await _context.Clients.FindAllAsync();
            return [.. data];
        }

        /// <inheritdoc/>
        public async Task<Client?> GetByRequestInfoAsync(string requestInfo) =>
            await _context.Clients.FindOneAsync(x => x.RequestInfo == requestInfo);

        /// <inheritdoc/>
        public async Task<bool> ExistsAsync(string requestInfo) =>
            await _context.Clients.ExistsAsync(x => x.RequestInfo == requestInfo);

        /// <inheritdoc/>
        public async Task InsertAsync(Client client) => _ = await _context.Clients.InsertAsync(client);

        /// <inheritdoc/>
        public async Task UpdateAsync(Client client) => _ = await _context.Clients.UpdateAsync(client);

        /// <inheritdoc/>
        public async Task<IReadOnlyList<Identify>> GetUnidentifiedAsync()
        {
            IEnumerable<Identify> data = await _context.Identify.FindAllAsync();
            return [.. data];
        }

        /// <inheritdoc/>
        public async Task<bool> IdentifyExistsAsync(string requestInfo) =>
            await _context.Identify.ExistsAsync(x => x.RequestInfo == requestInfo);

        /// <inheritdoc/>
        public async Task<Identify?> GetIdentifyAsync(string requestInfo) =>
            await _context.Identify.FindOneAsync(x => x.RequestInfo == requestInfo);

        /// <inheritdoc/>
        public async Task AddIdentifyAsync(Identify identify) => _ = await _context.Identify.InsertAsync(identify);

        /// <inheritdoc/>
        public async Task RemoveIdentifyAsync(string requestInfo) =>
            _ = await _context.Identify.DeleteManyAsync(x => x.RequestInfo == requestInfo);
    }
}
