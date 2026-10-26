using Lanternly.Core;
using Xunit;
namespace Lanternly.Core.Tests;

/// <summary>0.1.3 review fixes: validation on load, clock-set-back dedupe, quarantine, shared grace, unified id types.</summary>
public class ScheduleRobustnessTests
{
    static readonly TimeZoneInfo Ny = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    static string Entry(object id, object hour, object minute, object? days = null, string action = "on", string target = "all") =>
        $$"""{"Id":{{id}},"Enabled":true,"Hour":{{hour}},"Minute":{{minute}},"Days":{{days ?? 127}},"Action":"{{action}}","HomeId":7,"Target":"{{target}}"}""";
    static string File(params string[] e) => $$"""{"Format":1,"Schedules":[{{string.Join(",", e)}}]}""";

    [Fact]
    public void InvalidEntriesAreSkippedNotFatal()
    {
        var (l, bad) = Schedules.Parse(File(
            Entry(1, 7, 0), Entry(2, 24, 0), Entry(3, 7, 60), Entry(4, -1, 0), Entry(5, 7, 0, 128), Entry(6, 7, 0, -1),
            Entry(7, 7, 0, action: "Disco"), Entry(8, 7, 0, target: "room:x"), Entry(9, "\"seven\"", 0), "42", Entry(1, 8, 0), Entry(10, 23, 59, 0, target: "bulb:3")));
        Assert.Equal([1L, 10L], l.Select(s => s.Id));
        Assert.Equal(10, bad);
    }

    [Fact]
    public void BoundaryValuesAccepted()
    {
        Assert.Null(Schedules.InvalidReason(new Schedule { Id = 1, Hour = 0, Minute = 0, Days = 0, Action = "off" }));
        Assert.Null(Schedules.InvalidReason(new Schedule { Id = 1, Hour = 23, Minute = 59, Days = 0x7f, Target = "bulb:255" }));
        Assert.NotNull(Schedules.InvalidReason(new Schedule { Id = 0, Hour = 1 }));
    }

    [Fact]
    public void ClockSetBackDoesNotReplay()
    {
        var s = new Schedule { Id = 1, Hour = 7, Minute = 0 };
        var fired = Schedules.Resolve(new DateTime(2026, 10, 14, 7, 0, 0), Ny);
        var ms = fired.ToUnixTimeMilliseconds();
        Assert.True(Schedules.IsReplay(fired, ms));
        Assert.True(Schedules.IsReplay(fired.AddSeconds(-1), ms));
        Assert.False(Schedules.IsReplay(fired.AddDays(1), ms));
        Assert.False(Schedules.IsReplay(fired, 0));
        Assert.Equal(Schedules.Resolve(new DateTime(2026, 10, 15, 7, 0, 0), Ny), Schedules.NextFireAfter(s, fired.AddMinutes(-10), Ny, ms));
        Assert.Equal(fired, Schedules.NextFireAfter(s, fired.AddMinutes(-10), Ny, ms - 86_400_000));
        Assert.Equal(fired, Schedules.NextFireAfter(s, fired.AddMinutes(-10), Ny, 0));
    }

    [Fact]
    public void UnreadableFileIsQuarantinedNotOverwritten()
    {
        var dir = Directory.CreateTempSubdirectory("lt").FullName;
        try
        {
            var f = Path.Combine(dir, "schedules.json");
            Assert.Empty(Schedules.LoadOrQuarantine(f, "x").Schedules);
            System.IO.File.WriteAllText(f, "{not json");
            var (l, bad, q) = Schedules.LoadOrQuarantine(f, "20261025-200000");
            Assert.Empty(l); Assert.Equal(0, bad);
            Assert.Equal("schedules.json.corrupt-20261025-200000", q);
            Assert.False(System.IO.File.Exists(f));
            Assert.Equal("{not json", System.IO.File.ReadAllText(Path.Combine(dir, q!)));
            System.IO.File.WriteAllText(f, File(Entry(1, 7, 0), Entry(2, 99, 0)));
            var ok = Schedules.LoadOrQuarantine(f, "y");
            Assert.Single(ok.Schedules); Assert.Equal(1, ok.Skipped); Assert.Null(ok.Quarantined); Assert.True(System.IO.File.Exists(f));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void GraceIsTenMinutesLikeAndroid()
    {
        Assert.Equal(TimeSpan.FromMinutes(10), Schedules.Grace);
        var due = DateTimeOffset.Now;
        Assert.True(Schedules.OnTime(due, due.AddMinutes(9)));
        Assert.False(Schedules.OnTime(due, due.AddMinutes(11)));
    }

    [Fact]
    public void HomeIdIs64BitLikeAndroid()
    {
        var l = Schedules.FromJson(File(Entry(5, 7, 0).Replace("\"HomeId\":7", "\"HomeId\":5000000000")));
        Assert.Equal(5_000_000_000L, l.Single().HomeId);
        Assert.Equal(typeof(long), typeof(Schedule).GetProperty(nameof(Schedule.HomeId))!.PropertyType);
    }

    [Fact] // saving must not delete entries that were skipped as invalid at load
    public void SaveKeepsSkippedInvalidEntries()
    {
        var file = "{\"Format\":1,\"Schedules\":[{\"Id\":1,\"Enabled\":true,\"Hour\":7,\"Minute\":0,\"Days\":127,\"Action\":\"on\",\"HomeId\":5,\"Target\":\"all\"},{\"Id\":2,\"Hour\":24,\"Minute\":0,\"Action\":\"on\",\"Target\":\"all\"},{\"Id\":\"x\"}]}";
        var (l, bad) = Schedules.Parse(file);
        Assert.Single(l); Assert.Equal(2, bad);
        l[0].Hour = 8;
        var saved = Schedules.ToJsonKeepingSkipped(l, file);
        var (l2, bad2) = Schedules.Parse(saved);
        Assert.Single(l2); Assert.Equal(8, l2[0].Hour); Assert.Equal(2, bad2);
        Assert.Contains("\"Hour\": 24", saved);
        Assert.Equal(Schedules.ToJson(l), Schedules.ToJsonKeepingSkipped(l, null));
    }
}
