using SPI.Twamp.Server.Abstractions;

namespace SPI.Twamp.Server.Contracts;

public record CollectorCatalog(string[] Probes);
public sealed record CollectorPeer(string Id, string Url);
public sealed record CollectorRoute(string Probe, long Revision, CollectorPeer[] Members);
public sealed record PeerPresence(string Id, long Revision, bool Ready);
public record CollectorSnapshot(Guid Instance, long Version,
    Dictionary<string,ProbePollState> States, Dictionary<Guid,TaskLastResult> LastResults,DateTime? LastUploadAt = null);
