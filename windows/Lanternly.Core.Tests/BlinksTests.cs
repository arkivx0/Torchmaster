using Lanternly.Core;
using Xunit;
namespace Lanternly.Core.Tests;

public class BlinksTests
{
    [Fact]
    public void StateSurvivesWithoutTheRowAndSecondTapStops()
    {
        var b = new Blinks<string>();
        b.Start("lamp");
        Assert.True(b.IsActive("lamp")); // a rebuilt row reads this, so the next tap is a stop
        Assert.True(b.Stop("lamp"));
        Assert.False(b.IsActive("lamp"));
        Assert.False(b.Stop("lamp"));
    }

    [Fact]
    public void FailedStartResetsState()
    {
        var b = new Blinks<string>();
        var t = b.Start("lamp");
        b.Stop("lamp"); // what the find button does when the start write throws
        Assert.False(b.IsActive("lamp"));
        Assert.True(t.IsCancellationRequested);
    }

    [Fact]
    public void StaleTimerDoesNotStopANewerBlink()
    {
        var b = new Blinks<string>();
        var old = b.Start("lamp"); b.Stop("lamp");
        var cur = b.Start("lamp");
        Assert.False(b.Expire("lamp", old));
        Assert.True(b.IsActive("lamp"));
        Assert.True(b.Expire("lamp", cur));
        Assert.False(b.IsActive("lamp"));
    }

    [Fact]
    public void DrainReturnsPendingStopsAndCancelsTimers()
    {
        var b = new Blinks<string>();
        var t1 = b.Start("a"); var t2 = b.Start("b");
        Assert.Equal(["a", "b"], b.Drain().OrderBy(x => x));
        Assert.True(t1.IsCancellationRequested && t2.IsCancellationRequested);
        Assert.Equal(0, b.Count);
    }
}
