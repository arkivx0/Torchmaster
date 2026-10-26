using Lanternly.Core;
using Xunit;
namespace Lanternly.Core.Tests;

public class ScheduleTests
{
    static readonly TimeZoneInfo Ny = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    static DateTimeOffset At(int y, int mo, int d, int h, int mi) => Schedules.Resolve(new DateTime(y, mo, d, h, mi, 0), Ny);
    static Schedule S(int h, int m, int days = Schedules.EveryDay) => new() { Hour = h, Minute = m, Days = days };

    [Fact]
    public void NextFireLaterTodayOrTomorrow()
    {
        var now = At(2026, 10, 14, 6, 0); // Wednesday
        Assert.Equal(At(2026, 10, 14, 7, 0), Schedules.NextFire(S(7, 0), now, Ny));
        Assert.Equal(At(2026, 10, 15, 5, 0), Schedules.NextFire(S(5, 0), now, Ny));
        Assert.Equal(At(2026, 10, 15, 6, 0), Schedules.NextFire(S(6, 0), now, Ny)); // exactly now: next one
    }

    [Fact]
    public void DayOfWeekMask()
    {
        var wed = At(2026, 10, 14, 8, 0);
        var weekdays = 0x1f; var mondayOnly = Schedules.DayBit(DayOfWeek.Monday);
        Assert.Equal(At(2026, 10, 15, 7, 0), Schedules.NextFire(S(7, 0, weekdays), wed, Ny));
        Assert.Equal(At(2026, 10, 19, 7, 0), Schedules.NextFire(S(7, 0, mondayOnly), wed, Ny));
        Assert.Equal(At(2026, 10, 17, 9, 0), Schedules.NextFire(S(9, 0, 0x60), wed, Ny)); // Sat
        Assert.Equal(At(2026, 10, 21, 7, 0), Schedules.NextFire(S(7, 0, Schedules.DayBit(DayOfWeek.Wednesday)), wed, Ny)); // passed today: next week
        Assert.Null(Schedules.NextFire(S(9, 0, 0), wed, Ny));
        Assert.Equal(1, Schedules.DayBit(DayOfWeek.Monday)); Assert.Equal(64, Schedules.DayBit(DayOfWeek.Sunday));
    }

    [Fact]
    public void DstSpringForwardGapShiftsByTheGap()
    {
        // 2026-03-08 02:00 EST → 03:00 EDT. 02:30 does not exist; it runs at 03:30 EDT.
        var now = new DateTimeOffset(2026, 3, 8, 1, 0, 0, TimeSpan.FromHours(-5));
        var t = Schedules.NextFire(S(2, 30), now, Ny)!.Value;
        Assert.Equal(new DateTimeOffset(2026, 3, 8, 3, 30, 0, TimeSpan.FromHours(-4)), t);
        // 07:00 across the change keeps wall-clock time
        Assert.Equal(new DateTimeOffset(2026, 3, 8, 7, 0, 0, TimeSpan.FromHours(-4)), Schedules.NextFire(S(7, 0), now, Ny));
    }

    [Fact]
    public void DstFallBackRunsOnce()
    {
        // 2026-11-01 02:00 EDT → 01:00 EST. 01:30 happens twice; run only the first.
        var now = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.FromHours(-4));
        var first = Schedules.NextFire(S(1, 30), now, Ny)!.Value;
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 1, 30, 0, TimeSpan.FromHours(-4)), first);
        var second = Schedules.NextFire(S(1, 30), first, Ny)!.Value;
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 1, 30, 0, TimeSpan.FromHours(-5)), second);
        Assert.Equal(TimeSpan.FromHours(25), Schedules.NextFire(S(7, 0), At(2026, 10, 31, 7, 0), Ny) - At(2026, 10, 31, 7, 0));
    }

    [Fact]
    public void MissedRunsAreSkipped()
    {
        var due = At(2026, 10, 14, 7, 0);
        Assert.True(Schedules.OnTime(due, due.AddMinutes(4)));
        Assert.False(Schedules.OnTime(due, due.AddMinutes(30)));
        Assert.False(Schedules.OnTime(due, due.AddMinutes(-1)));
    }

    [Fact]
    public void TargetsAndJsonRoundTrip()
    {
        var h = new Home { Bulbs = [new Bulb { Name = "A", MeshId = 2 }, new Bulb { Name = "B", MeshId = 3 }], Rooms = [new Room { Id = 4, Name = "R", MeshIds = [3] }] };
        Assert.Equal(2, Schedules.Targets(h, new Schedule { Target = "all" }).Count);
        Assert.Equal("B", Schedules.Targets(h, new Schedule { Target = "room:4" }).Single().Name);
        Assert.Equal("A", Schedules.Targets(h, new Schedule { Target = "bulb:2" }).Single().Name);
        Assert.Empty(Schedules.Targets(h, new Schedule { Target = "bulb:9" }));
        var l = new List<Schedule> { new() { Id = 1, Hour = 21, Minute = 5, Days = 0x60, Action = "Red", HomeId = 7, Target = "bulb:2", Enabled = false } };
        var back = Schedules.FromJson(Schedules.ToJson(l)).Single();
        Assert.Equal((21, 5, 0x60, "Red", 7u, "bulb:2", false), (back.Hour, back.Minute, back.Days, back.Action, back.HomeId, back.Target, back.Enabled));
        Assert.Equal("21:05 · Weekends · Scene: Red · A", Schedules.Describe(h, back));
    }

    [Fact] // the Red schedule action sets full brightness, not just color
    public async Task RedSceneScheduleSendsPowerFullBrightnessRed()
    {
        var link = new RecLink(); var h = new Home { Bulbs = [new Bulb { Name = "A", MeshId = 2, Type = DeviceCaps.AllControls }] };
        var r = await Schedules.RunAsync(new Router([link]), h, new Schedule { Action = "Red", Target = "all" });
        Assert.True(r.AllOk);
        Assert.Equal(new Cmd[] { new Cmd.Power(true), new Cmd.Brightness(100), new Cmd.Rgb(255, 0, 0) }, link.Sent);
        link.Sent.Clear();
        await Schedules.RunAsync(new Router([link]), h, new Schedule { Action = "off", Target = "bulb:2" });
        Assert.Equal(new Cmd[] { new Cmd.Power(false) }, link.Sent);
    }

    sealed class RecLink : ILink
    {
        public readonly List<Cmd> Sent = [];
        public Via Kind => Via.Ble;
        public event Action<Home?, BulbState>? StateChanged { add { } remove { } }
        public bool CanReach(Home h, Bulb b) => true;
        public Task SendAsync(Home h, Bulb b, Cmd c, CancellationToken ct = default) { Sent.Add(c); return Task.CompletedTask; }
    }
}
