using SPI.Twamp.Server.Abstractions;

namespace SPI.Twamp.Server.Contracts;

public record CollectorCatalog(string[] Probes);
public record CollectorSnapshot(Guid Instance, long Version,
    Dictionary<string,ProbePollState> States, Dictionary<Guid,TaskLastResult> LastResults);
