namespace SPI.Twamp.Server.Parser
{
    /// <summary>
    /// 
    /// </summary>
    public class TwPingStats
    {
        /// <summary>
        /// Gets or sets the identifier.
        /// </summary>
        /// <value>
        /// The identifier.
        /// </value>
        public Guid? Id{ get; set; }
        /// <summary>
        /// Gets or sets the title.
        /// </summary>
        /// <value>
        /// The title.
        /// </value>
        public string? Title { get; set; }
        /// <summary>
        /// Gets or sets from host.
        /// </summary>
        /// <value>
        /// From host.
        /// </value>
        public string? FromHost { get; set; }
        /// <summary>
        /// Gets or sets from port.
        /// </summary>
        /// <value>
        /// From port.
        /// </value>
        public int? FromPort { get; set; }
        /// <summary>
        /// Converts to host.
        /// </summary>
        /// <value>
        /// To host.
        /// </value>
        public string? ToHost { get; set; }
        /// <summary>
        /// Converts to port.
        /// </summary>
        /// <value>
        /// To port.
        /// </value>
        public int? ToPort { get; set; }

        /// <summary>
        /// Gets or sets the sid.
        /// </summary>
        /// <value>
        /// The sid.
        /// </value>
        public string? Sid { get; set; }
        /// <summary>
        /// Gets or sets the first.
        /// </summary>
        /// <value>
        /// The first.
        /// </value>
        public DateTime? First { get; set; }
        /// <summary>
        /// Gets or sets the last.
        /// </summary>
        /// <value>
        /// The last.
        /// </value>
        public DateTime? Last { get; set; }
        /// <summary>
        /// Gets or sets the sent.
        /// </summary>
        /// <value>
        /// The sent.
        /// </value>
        public int? Sent { get; set; }
        /// <summary>
        /// Gets or sets the lost.
        /// </summary>
        /// <value>
        /// The lost.
        /// </value>
        public int? Lost { get; set; }
        /// <summary>
        /// Gets or sets the loss percent.
        /// </summary>
        /// <value>
        /// The loss percent.
        /// </value>
        public double? LossPercent { get; set; }

        /// <summary>
        /// Gets or sets the RTT minimum.
        /// </summary>
        /// <value>
        /// The RTT minimum.
        /// </value>
        public double? RttMin { get; set; }
        /// <summary>
        /// Gets or sets the RTT median.
        /// </summary>
        /// <value>
        /// The RTT median.
        /// </value>
        public double? RttMedian { get; set; }
        /// <summary>
        /// Gets or sets the RTT maximum.
        /// </summary>
        /// <value>
        /// The RTT maximum.
        /// </value>
        public double? RttMax { get; set; }

        /// <summary>
        /// Gets or sets the send minimum.
        /// </summary>
        /// <value>
        /// The send minimum.
        /// </value>
        public double? SendMin { get; set; }
        /// <summary>
        /// Gets or sets the send median.
        /// </summary>
        /// <value>
        /// The send median.
        /// </value>
        public double? SendMedian { get; set; }
        /// <summary>
        /// Gets or sets the send maximum.
        /// </summary>
        /// <value>
        /// The send maximum.
        /// </value>
        public double? SendMax { get; set; }

        /// <summary>
        /// Gets or sets the reflect minimum.
        /// </summary>
        /// <value>
        /// The reflect minimum.
        /// </value>
        public double? ReflectMin { get; set; }
        /// <summary>
        /// Gets or sets the reflect median.
        /// </summary>
        /// <value>
        /// The reflect median.
        /// </value>
        public double? ReflectMedian { get; set; }
        /// <summary>
        /// Gets or sets the reflect maximum.
        /// </summary>
        /// <value>
        /// The reflect maximum.
        /// </value>
        public double? ReflectMax { get; set; }

        /// <summary>
        /// Gets or sets the reflect proc minimum.
        /// </summary>
        /// <value>
        /// The reflect proc minimum.
        /// </value>
        public double? ReflectProcMin { get; set; }
        /// <summary>
        /// Gets or sets the reflect proc maximum.
        /// </summary>
        /// <value>
        /// The reflect proc maximum.
        /// </value>
        public double? ReflectProcMax { get; set; }

        /// <summary>
        /// Gets or sets the two way jitter.
        /// </summary>
        /// <value>
        /// The two way jitter.
        /// </value>
        public double? TwoWayJitter { get; set; }
        /// <summary>
        /// Gets or sets the send jitter.
        /// </summary>
        /// <value>
        /// The send jitter.
        /// </value>
        public double? SendJitter { get; set; }
        /// <summary>
        /// Gets or sets the reflect jitter.
        /// </summary>
        /// <value>
        /// The reflect jitter.
        /// </value>
        public double? ReflectJitter { get; set; }

        /// <summary>
        /// Gets or sets the send hops.
        /// </summary>
        /// <value>
        /// The send hops.
        /// </value>
        public int? SendHops { get; set; }
        /// <summary>
        /// Gets or sets the reflect hops.
        /// </summary>
        /// <value>
        /// The reflect hops.
        /// </value>
        public int? ReflectHops { get; set; }
        /// <summary>
        /// Gets or sets the errors.
        /// </summary>
        /// <value>
        /// The errors.
        /// </value>
        public string? Errors { get; set; }
    }
}