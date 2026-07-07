// Ignore Spelling: SPI Twamp

using LiteDB;

namespace SPI.Twamp.Server.Contracts
{
    /// <summary>
    /// 
    /// </summary>
    public class ActionData
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
        /// Gets or sets the creation.
        /// </summary>
        /// <value>
        /// The creation.
        /// </value>
        public DateTime? Creation { get; set; }

        /// <summary>
        /// Gets or sets the identifier.
        /// </summary>
        /// <value>
        /// The identifier.
        /// </value>
        public Guid TaskId { get; set; }
        /// <summary>
        /// Gets or sets the end node.
        /// </summary>
        /// <value>
        /// The end node.
        /// </value>
        public string EndNode { get; set; } = "0.0.0.0.0";

        /// <summary>
        /// Gets or sets the request information.
        /// </summary>
        /// <value>
        /// The request information.
        /// </value>
        public string RequestInfo { get; set; } = "0.0.0.0";
        /// <summary>
        /// Gets or sets the console.
        /// </summary>
        /// <value>
        /// The console.
        /// </value>
        public string Console { get; set; } = "";
        /// <summary>
        /// Gets or sets the error console.
        /// </summary>
        /// <value>
        /// The error console.
        /// </value>
        public string ErrorConsole { get; set; } = "";
    }
}
