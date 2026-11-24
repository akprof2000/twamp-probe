using SPI.Twamp.HighAvailability;
using Xunit;

namespace SPI.Twamp.Tests;

public class CollectionPlanTests
{
    private static readonly CollectorNode[] Nodes = [new("a", "http://a", "host-a"), new("b", "http://b", "host-b")];
    private static ProbePair Pair(int i) => new($"http://probe{i}", $"Проба {i}", "a", "b");

    [Fact]
    public void BalancesWorkAndPreservesAssignmentsWhenUnchanged()
    {
        ProbePair[] pairs = [Pair(1), Pair(2), Pair(3), Pair(4)];
        var weights = pairs.Zip(new[] { 80d, 60, 40, 20 }).ToDictionary(p => p.First.Url, p => p.Second);
        var draft = new PlanDraft(0, Nodes, pairs);
        var first = PlanBuilder.Build(draft, CollectionPlan.Empty, weights);
        Assert.All(first.Assignments.GroupBy(a => a.Primary), g => Assert.Equal(100d, g.Sum(a => weights[a.Probe.Url])));
        var again = PlanBuilder.Build(draft with { Version = 1 }, first, weights);
        Assert.Equal(first.Assignments, again.Assignments);
    }

    [Fact]
    public void RespectsPinStandbyAndIndependentHosts()
    {
        var p = Pair(1) with { Pin = "b" };
        var draft = new PlanDraft(0, Nodes, [p]);
        Assert.Equal("b", PlanBuilder.Build(draft, CollectionPlan.Empty, new Dictionary<string, double>()).Assignments.Single().Primary);
        foreach (var invalid in new[]{draft with{Collectors=[Nodes[0],Nodes[1] with{Machine="HOST-A"}]},
                    draft with{Collectors=[Nodes[0],Nodes[1] with{Standby=true}]},draft with{Probes=[p with{First="unknown"}]}})
            Assert.Throws<ArgumentException>(() => PlanBuilder.Build(invalid, CollectionPlan.Empty, new Dictionary<string, double>()));
    }
}
