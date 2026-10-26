using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lanternly.Core;

/// <summary>
/// A local schedule: at Hour:Minute (local time) on the chosen days, turn the target on or off or apply a scene.
/// Runs on this device only (no cloud). Days: bit 0 = Monday … bit 6 = Sunday. Target: "all", "room:&lt;id&gt;" or "bulb:&lt;meshId&gt;".
/// Action: "on", "off" or a scene name (e.g. "Red").
/// </summary>
public sealed class Schedule
{
    public long Id { get; set; }
    public bool Enabled { get; set; } = true;
    public int Hour { get; set; }
    public int Minute { get; set; }
    public int Days { get; set; } = Schedules.EveryDay;
    public string Action { get; set; } = "on";
    public uint HomeId { get; set; }
    public string Target { get; set; } = "all";
}

public static class Schedules
{
    public const int EveryDay = 0x7f;
    /// <summary>A run is skipped when it would start more than this late (e.g. PC asleep, app closed).</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(5);
    public static readonly string[] DayNames = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];
    public static readonly string[] Actions = ["on", "off", .. Scenes.All.Where(s => s.Name != "Off").Select(s => s.Name)];

    public static int DayBit(DayOfWeek d) => 1 << (((int)d + 6) % 7);

    /// <summary>
    /// Next time strictly after <paramref name="now"/>. DST: a time skipped by spring-forward runs shifted by the gap
    /// (02:30 → 03:30); a time repeated by fall-back runs once, at its first occurrence. Null if no day is selected.
    /// </summary>
    public static DateTimeOffset? NextFire(Schedule s, DateTimeOffset now, TimeZoneInfo tz)
    {
        if ((s.Days & EveryDay) == 0) return null;
        var today = TimeZoneInfo.ConvertTime(now, tz).Date;
        for (int i = 0; i <= 8; i++)
        {
            var day = today.AddDays(i);
            if ((s.Days & DayBit(day.DayOfWeek)) == 0) continue;
            var t = Resolve(day.AddHours(s.Hour).AddMinutes(s.Minute), tz);
            if (t > now) return t;
        }
        return null;
    }

    /// <summary>Local wall time → instant, with java.time ZonedDateTime.of semantics for gaps and overlaps.</summary>
    public static DateTimeOffset Resolve(DateTime local, TimeZoneInfo tz)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (tz.IsInvalidTime(local)) local += tz.GetUtcOffset(local.AddHours(6)) - tz.GetUtcOffset(local.AddHours(-6));
        if (tz.IsAmbiguousTime(local)) return new DateTimeOffset(local, tz.GetAmbiguousTimeOffsets(local).Max());
        return new DateTimeOffset(local, tz.GetUtcOffset(local));
    }

    /// <summary>True if a run due at <paramref name="due"/> may still start at <paramref name="now"/>.</summary>
    public static bool OnTime(DateTimeOffset due, DateTimeOffset now) => now >= due && now - due <= Grace;

    public static List<Bulb> Targets(Home h, Schedule s)
    {
        if (s.Target == "all") return [.. h.Bulbs];
        if (s.Target.StartsWith("room:") && int.TryParse(s.Target[5..], out var r))
            return h.Rooms.FirstOrDefault(x => x.Id == r) is { } room ? h.Bulbs.Where(b => room.MeshIds.Contains(b.MeshId)).ToList() : [];
        if (s.Target.StartsWith("bulb:") && ushort.TryParse(s.Target[5..], out var m)) return h.Bulbs.Where(b => b.MeshId == m).ToList();
        return [];
    }

    public static string TargetName(Home? h, Schedule s) => s.Target switch
    {
        "all" => "All bulbs",
        var t when t.StartsWith("room:") => h?.Rooms.FirstOrDefault(r => "room:" + r.Id == t)?.Name ?? "(removed room)",
        var t when t.StartsWith("bulb:") => h?.Bulbs.FirstOrDefault(b => "bulb:" + b.MeshId == t)?.Name ?? "(removed bulb)",
        _ => "?",
    };

    public static string DaysText(int days) => (days & EveryDay) switch
    {
        EveryDay => "Every day",
        0x1f => "Weekdays",
        0x60 => "Weekends",
        0 => "No days",
        var d => string.Join(" ", DayNames.Where((_, i) => (d & (1 << i)) != 0)),
    };

    public static string ActionText(string a) => a switch { "on" => "Turn on", "off" => "Turn off", _ => "Scene: " + a };
    public static string Describe(Home? h, Schedule s) => $"{s.Hour:00}:{s.Minute:00} · {DaysText(s.Days)} · {ActionText(s.Action)} · {TargetName(h, s)}";

    /// <summary>Runs the action on the targets over the given router.</summary>
    public static async Task<BatchResult> RunAsync(Router r, Home h, Schedule s, CancellationToken ct = default)
    {
        var bulbs = Targets(h, s);
        if (s.Action == "on" || s.Action == "off") return await r.SendManyAsync(h, bulbs, new Cmd.Power(s.Action == "on"), ct);
        var scene = Scenes.All.FirstOrDefault(x => x.Name == s.Action) ?? throw new InvalidDataException("unknown scene " + s.Action);
        return await Scenes.ApplyAsync(r, h, scene, bulbs);
    }

    static readonly JsonSerializerOptions O = new() { WriteIndented = true };
    public static string ToJson(List<Schedule> l) => JsonSerializer.Serialize(new { Format = 1, Schedules = l }, O);
    public static List<Schedule> FromJson(string json)
    {
        using var d = JsonDocument.Parse(json);
        return d.RootElement.TryGetProperty("Schedules", out var a) ? a.Deserialize<List<Schedule>>() ?? [] : [];
    }
}
