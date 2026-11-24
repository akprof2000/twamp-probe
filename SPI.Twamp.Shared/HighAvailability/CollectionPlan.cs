namespace SPI.Twamp.HighAvailability;

public sealed record CollectorNode(string Id, string Url, string Machine, bool Standby = false, double Capacity = 100);
public sealed record ProbePair(string Url, string Name, string First, string Second, string? Pin = null)
{
    public bool Contains(string id) => First == id || Second == id;
}
public sealed record Assignment(ProbePair Probe, string Primary, long Generation);
public sealed record CollectionPlan(CollectorNode[] Collectors, Assignment[] Assignments, string[] Removed)
{
    public static CollectionPlan Empty => new([], [], []);
}
public sealed record VersionedPlan(int Version, CollectionPlan Plan);
public sealed record PlanDraft(int Version, CollectorNode[] Collectors, ProbePair[] Probes, string[]? Reassign = null,
    Dictionary<string, string>? Approved = null);

public static class PlanBuilder
{
    public static string Address(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("Требуется базовый HTTP(S)-адрес.");
        return uri.AbsoluteUri.TrimEnd('/');
    }

    public static CollectionPlan Build(PlanDraft draft, CollectionPlan previous, IReadOnlyDictionary<string, double> weights)
    {
        if (draft.Collectors == null || draft.Probes == null) throw new ArgumentException("Передайте съёмники и пробы.");
        if (draft.Collectors.Any(n => n == null) || draft.Probes.Any(p => p == null))
            throw new ArgumentException("В списке съёмников или проб есть пустая запись.");
        var nodes = draft.Collectors.Select(n => n with { Url = Address(n.Url), Machine = n.Machine?.Trim() ?? "" }).ToArray();
        if (nodes.Any(n => n.Id is not { Length: > 0 and <= 64 } || n.Id.Any(c => !char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c != '-' && c != '_')
            || n.Machine.Length == 0 || !double.IsFinite(n.Capacity) || n.Capacity <= 0))
            throw new ArgumentException("Проверьте ID, машину и производительность съёмника.");
        var byId = nodes.ToDictionary(n => n.Id);
        if (nodes.DistinctBy(n => n.Url).Count() != nodes.Length) throw new ArgumentException("Адреса съёмников должны различаться.");
        var probes = draft.Probes.Select(p => p with { Url = Address(p.Url), Name = p.Name?.Trim() ?? "" }).ToArray();
        if (probes.DistinctBy(p => p.Url).Count() != probes.Length) throw new ArgumentException("Адрес пробы повторяется.");
        var old = previous.Assignments.ToDictionary(a => a.Probe.Url);
        var load = nodes.ToDictionary(n => n.Id, n => 0d);
        var assigned = new List<Assignment>();
        double Weight(ProbePair p) => Math.Max(0.01, weights.GetValueOrDefault(p.Url, 1));
        foreach (var p in probes.OrderByDescending(p => !string.IsNullOrEmpty(p.Pin))
                     .ThenByDescending(Weight).ThenBy(p => p.Url, StringComparer.Ordinal))
        {
            if (p.Name.Length == 0 || !byId.TryGetValue(p.First ?? "", out var first) || !byId.TryGetValue(p.Second ?? "", out var second)
                || string.Equals(first.Machine, second.Machine, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"Для {p.Url} укажите имя и два съёмника на разных машинах.");
            old.TryGetValue(p.Url, out var before);
            var primary = new[] { first, second }.Where(n => !n.Standby && (string.IsNullOrEmpty(p.Pin) || p.Pin == n.Id))
                .OrderBy(n => (load[n.Id] + Weight(p)) / n.Capacity).ThenByDescending(n => before?.Primary == n.Id)
                .ThenBy(n => n.Id, StringComparer.Ordinal).FirstOrDefault()
                ?? throw new ArgumentException($"Для {p.Name} нет допустимого основного съёмника.");
            load[primary.Id] += Weight(p);
            var changed = before == null || before.Primary != primary.Id || before.Probe.First != p.First || before.Probe.Second != p.Second
                || (draft.Reassign?.Contains(p.Url) ?? false);
            assigned.Add(new(p, primary.Id, changed ? (long)draft.Version + 1 : before!.Generation));
        }
        var removed = previous.Removed.Concat(old.Keys).Except(probes.Select(p => p.Url)).Distinct().ToArray();
        return new(nodes, assigned.ToArray(), removed);
    }
}
