using SPI.Twamp.Collector;
using Xunit;

namespace SPI.Twamp.Tests;

public sealed class CollectorPriorityTests
{
    [Fact]
    public void WaitsThreeCyclesForEachHigherPriorityAndResetsAfterRecovery()
    {
        var firstReserve = new CollectorPriority(1);
        Assert.False(firstReserve.MayCollect([false]));
        Assert.False(firstReserve.MayCollect([false]));
        Assert.True(firstReserve.MayCollect([false]));
        Assert.False(firstReserve.MayCollect([true]));
        Assert.False(firstReserve.MayCollect([false]));
        var secondReserve = new CollectorPriority(2);
        for (int i = 0; i < 5; i++) Assert.False(secondReserve.MayCollect([false, false]));
        Assert.True(secondReserve.MayCollect([false, false]));
        Assert.False(secondReserve.MayCollect([false, true]));
        for (int i = 0; i < 2; i++) Assert.False(secondReserve.MayCollect([false, false]));
        Assert.True(secondReserve.MayCollect([false, false]));
    }
}
