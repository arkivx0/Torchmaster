using Lanternly.Core;
using Xunit;

namespace Lanternly.Core.Tests;

/// <summary>0.1.6: sunrise/sunset (NOAA; reference times from astral 3), native effect bytes, app-side rate limits, fades.</summary>
public class SunAndEffectsTests
{
    static TimeZoneInfo Tz(string id) => TimeZoneInfo.FindSystemTimeZoneById(id);
    static void Check(double lat, double lon, string zone, string date, string rise, string set)
    {
        var z = Tz(zone); var d = DateOnly.Parse(date); var loc = new SunLocation(lat, lon);
        foreach (var (isRise, want) in new[] { (true, rise), (false, set) })
        {
            var got = Sun.Event(d, loc, isRise, z) ?? throw new Exception($"no event {zone} {date}");
            var exp = Schedules.Resolve(d.ToDateTime(TimeOnly.Parse(want)), z);
            Assert.True(Math.Abs((got - exp).TotalSeconds) <= 120, $"{zone} {date} {(isRise ? "rise" : "set")}: got {got} want {exp}");
        }
    }

    [Fact] public void SeveralLatitudes()
    {
        Check(40.7128, -74.0060, "America/New_York", "2026-06-21", "05:25:17", "20:30:29");
        Check(51.5074, -0.1278, "Europe/London", "2026-12-21", "08:04:06", "15:53:03");
        Check(35.6762, 139.6503, "Asia/Tokyo", "2026-09-01", "05:13", "18:09");
        Check(0, 0, "UTC", "2026-03-20", "06:04:20", "18:10:30");
        Check(-33.8688, 151.2093, "Australia/Sydney", "2026-01-01", "05:47:44", "20:09:09");
        Check(21.3069, -157.8583, "Pacific/Honolulu", "2026-07-04", "05:54", "19:17");
        Check(64.1466, -21.9426, "Atlantic/Reykjavik", "2026-06-01", "03:23", "23:29");
    }

    [Fact] public void PolarAndDst()
    {
        var tromso = new SunLocation(69.6492, 18.9553); var oslo = Tz("Europe/Oslo");
        Assert.Null(Sun.Event(new DateOnly(2026, 6, 21), tromso, false, oslo));
        Assert.Null(Sun.Event(new DateOnly(2026, 12, 21), tromso, true, oslo));
        var s = new Schedule { Id = 1, Trigger = "sunset" };
        Assert.Equal(7, Schedules.NextFire(s, new DateTimeOffset(2026, 6, 21, 12, 0, 0, TimeSpan.FromHours(2)), oslo, tromso)!.Value.Month);
        Check(40.7128, -74.0060, "America/New_York", "2026-03-07", "06:20:42", "17:53:52");
        Check(40.7128, -74.0060, "America/New_York", "2026-03-08", "07:19:06", "18:54:58");
        Check(40.7128, -74.0060, "America/New_York", "2026-11-01", "06:26:38", "16:51:56");
        var c = Tz("America/New_York"); var loc = new SunLocation(40.7128, -74.0060);
        var t = Schedules.NextFire(new Schedule { Id = 1, Trigger = "sunrise", Offset = -30 }, new DateTimeOffset(2026, 3, 8, 0, 0, 0, TimeSpan.FromHours(-5)), c, loc)!.Value;
        Assert.Equal(8, t.Day); Assert.Equal(6, t.Hour); Assert.Equal(TimeSpan.FromHours(-4), t.Offset);
        Assert.Null(Schedules.NextFire(new Schedule { Id = 1, Trigger = "sunset" }, DateTimeOffset.Now, c, null));
    }

    [Fact] public void ScheduleJsonAndValidation()
    {
        var s = new Schedule { Id = 5, Days = 0x1f, Action = "fadein:15", HomeId = 3, Target = "room:2", Trigger = "sunrise", Offset = -20 };
        var b = Schedules.FromJson(Schedules.ToJson([s]))[0];
        Assert.Equal(("sunrise", -20, "fadein:15"), (b.Trigger, b.Offset, b.Action));
        Assert.Equal("time", Schedules.FromJson("""{"Schedules":[{"Id":1,"Hour":7,"Minute":0,"Days":127,"Action":"on","HomeId":1,"Target":"all"}]}""")[0].Trigger);
        foreach (var a in Schedules.AllActions) Assert.Null(Schedules.InvalidReason(new Schedule { Id = 1, Action = a }));
        foreach (var bad in new[] { "fadein:0", "fadein:61", "effect:42", "effect:x" }) Assert.NotNull(Schedules.InvalidReason(new Schedule { Id = 1, Action = bad }));
        Assert.NotNull(Schedules.InvalidReason(new Schedule { Id = 1, Trigger = "noon" }));
        Assert.NotNull(Schedules.InvalidReason(new Schedule { Id = 1, Offset = 500 }));
        Assert.StartsWith("Sunrise −20 min · Weekdays · Fade in over 15 min (wake-up)", Schedules.Describe(null, s));
        Assert.Equal(new SunLocation(39.74, -104.99), SunLocation.Parse(" 39,74 ", "-104.99")); // Denver city centre
        Assert.Null(SunLocation.Parse("91", "0"));
    }

    [Fact] public void NativeEffectBytes()
    {
        Assert.Equal(new byte[] { 7, 1, 1, 0x55 }, Effects.Data(1, 0x55));
        Assert.Equal(new byte[] { 7, 1, 67, 0 }, Effects.Data(67, 0));
        Assert.Equal(new byte[] { 7, 0, 0, 9 }, Effects.Data(0, 9));
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 65, 66, 67 }, Effects.All.Select(e => e.Id));
    }

    sealed class Rec : ILink
    {
        public List<(ushort, Cmd)> Sent = [];
        public Via Kind => Via.Ble;
        public event Action<Home?, BulbState>? StateChanged { add { } remove { } }
        public bool CanReach(Home h, Bulb b) => true;
        public Task SendAsync(Home h, Bulb b, Cmd c, CancellationToken ct = default) { Sent.Add((b.MeshId, c)); return Task.CompletedTask; }
    }

    [Fact] public async Task AppEffectsRateLimitedAndSkipUnknownAddress()
    {
        foreach (var n in AppEffects.All) for (int k = 1; k <= 20; k++) { var ms = AppEffects.StepMs(n, k); Assert.True(ms >= AppEffects.MinStepMs); Assert.True(k * 1000.0 / ms <= AppEffects.MaxCmdsPerSec + 1e-9); }
        var link = new Rec(); var h = new Home { Id = 1, Bulbs = [new Bulb { Name = "A", MeshId = 1, Type = -1 }, new Bulb { Name = "Z", MeshId = 0, Type = -1 }] };
        using var cts = new CancellationTokenSource(); int loops = 0;
        await AppEffects.RunAsync(new Router([link]), h, h.Bulbs, "Color cycle", cts.Token, (ms, c) => { Assert.True(ms >= AppEffects.MinStepMs); if (++loops >= 3) cts.Cancel(); return Task.CompletedTask; });
        Assert.Equal(3, link.Sent.Count); Assert.All(link.Sent, x => Assert.Equal(1, x.Item1));
        Assert.Equal(new Cmd.Rgb(255, 0, 0), link.Sent[0].Item2);
    }

    [Fact] public async Task FadesPlanAndRun()
    {
        var p = Fades.Plan(1, 100, 10);
        Assert.Equal((1, 100, 21, 600_000L), (p[0].Pct, p[^1].Pct, p.Count, p[^1].AtMs));
        Assert.All(p.Zip(p.Skip(1)), x => Assert.True(x.Second.AtMs - x.First.AtMs >= Fades.MinStepMs && x.Second.Pct > x.First.Pct));
        Assert.Equal(80, Fades.Plan(80, 1, 60).Count);
        var link = new Rec(); var h = new Home { Id = 1, Bulbs = [new Bulb { Name = "A", MeshId = 1, Type = -1 }] };
        long t = 0;
        var r = await Fades.RunAsync(new Router([link]), h, h.Bulbs, true, 5, default, () => t, (ms, c) => { t += ms; return Task.CompletedTask; });
        Assert.True(r.AllOk); Assert.Equal(300_000L, t);
        Assert.Equal(new Cmd.Brightness(1), link.Sent[0].Item2); Assert.Equal(new Cmd.Power(true), link.Sent[1].Item2); Assert.Equal(new Cmd.Brightness(100), link.Sent[^1].Item2);
        // fade out cancelled early: jumps to 1 % and turns off
        link.Sent.Clear(); t = 0; h.Bulbs[0].State = new BulbState(1, true, true, 60, 50, (0, 0, 0));
        using var cts = new CancellationTokenSource();
        await Fades.RunAsync(new Router([link]), h, h.Bulbs, false, 10, cts.Token, () => t, (ms, c) => { t += ms; if (t >= 60_000) { cts.Cancel(); throw new OperationCanceledException(); } return Task.CompletedTask; });
        Assert.Equal(new Cmd.Brightness(60), link.Sent[0].Item2); Assert.Equal(new Cmd.Power(false), link.Sent[^1].Item2); Assert.Equal(new Cmd.Brightness(1), link.Sent[^2].Item2);
    }

    [Fact] public async Task ScheduleEffectOnlyColorBulbs()
    {
        var link = new Rec();
        var h = new Home { Id = 1, Bulbs = [new Bulb { Name = "C", MeshId = 1, Type = -1 }, new Bulb { Name = "W", MeshId = 2, Type = 99999 }, new Bulb { Name = "U", MeshId = 0, Type = -1 }] };
        var r = await Schedules.RunAsync(new Router([link]), h, new Schedule { Id = 1, Action = "effect:5", HomeId = 1 });
        Assert.Equal(1, r.Ok); Assert.Single(link.Sent); Assert.Equal(new Cmd.Effect(5), link.Sent[0].Item2);
    }
}
