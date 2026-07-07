// Ignore Spelling: SPI Twamp

using LiteDB;

namespace SPI.Twamp.Server.Contracts
{
    /// <summary>
    /// 
    /// </summary>
    /// <seealso cref="SPI.Twamp.Server.Contracts.Identify" />
    public class Client: Identify
    {
        /// <summary>
        /// Gets or sets the identifier.
        /// </summary>
        /// <value>
        /// The identifier.
        /// </value>
        [BsonId]
        public ObjectId? Id { get; set; }
        /// <summary>
        /// Gets or sets the name.
        /// </summary>
        /// <value>
        /// The name.
        /// </value>
        public string Name { get; set; } = "New One";
    }
}
