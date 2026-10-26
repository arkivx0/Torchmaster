using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lanternly.Core;

/// <summary>
/// A local schedule: at Hour:Minute (local time) on the chosen days, turn the target on or off or apply a scene.
/// Runs on this device only (no cloud). Days: bit 0 = Monday … bit 6 = Sunday. Target: "all", "room:&lt;id&gt;" or "bulb:&lt;meshId&gt;".
/// Action: "on", "off" or a scene name (e.g. "Red"). Same JSON shape and field types as the Android app (Id and HomeId are 64-bit).
/// </summary>
public sealed class Schedule
{
    public long Id { get; set; }
    public bool Enabled { get; set; } = true;
    public int Hour { get; set; }
    public int Minute { get; set; }
    public int Days { get; set; } = Schedules.EveryDay;
    public string Action { get; set; } = "on";
    public long HomeId { get; set; }
    public string Target { get; set; } = "all";
    /// <summary>"time" (default; 0.1.5 files have none), "sunrise" or "sunset". Hour/Minute are ignored for sun triggers.</summary>
    public string Trigger { get; set; } = "time";
    /// <summary>Minutes after (+) or before (−) sunrise/sunset, −180..180.</summary>
    public int Offset { get; set; }
}

public static class Schedules
{
    public const int EveryDay = 0x7f;
    /// <summary>A run is skipped when it would start more than this late (e.g. PC asleep, app closed).</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(10);
    public static readonly string[] DayNames = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];
    public static readonly string[] Actions = ["on", "off", .. Scenes.All.Where(s => s.Name != "Off").Select(s => s.Name)];

    public static readonly string[] Triggers = ["time", "sunrise", "sunset"];
    public const int MaxOffset = 180;
    public static readonly int[] FadeMinutes = [5, 10, 15, 20, 30, 45, 60];
    /// <summary>Every action the editor offers: on/off, scenes, native effects and stop, fades. Actions ("effect:id", "fadein:min", "fadeout:min") as in Schedules.kt.</summary>
    public static readonly string[] AllActions = [.. Actions, .. Effects.All.Select(e => $"effect:{e.Id}"), "effect:0", .. FadeMinutes.Select(m => $"fadein:{m}"), .. FadeMinutes.Select(m => $"fadeout:{m}")];
    static readonly System.Text.RegularExpressions.Regex EffectRx = new(@"^effect:(\d{1,3})$"), FadeRx = new(@"^fade(in|out):(\d{1,2})$");
    public static bool ValidAction(string? a) => a != null && (Actions.Contains(a)
        || EffectRx.Match(a) is { Success: true } m && (int.Parse(m.Groups[1].Value) is var id && (id == 0 || Effects.Find(id) != null))
        || FadeRx.Match(a) is { Success: true } f && int.Parse(f.Groups[2].Value) is >= 1 and <= Fades.MaxMinutes);
    public static int? FadeMins(string a) => FadeRx.Match(a) is { Success: true } f ? int.Parse(f.Groups[2].Value) : null;
    /// <summary>Extra run time beyond one command per bulb (fades).</summary>
    public static TimeSpan ExtraRun(Schedule s) => FadeMins(s.Action) is { } m ? Fades.Duration(m) : TimeSpan.Zero;

    public static int DayBit(DayOfWeek d) => 1 << (((int)d + 6) % 7);

    /// <summary>
    /// Next time strictly after <paramref name="now"/>. DST: a time skipped by spring-forward runs shifted by the gap
    /// (02:30 → 03:30); a time repeated by fall-back runs once, at its first occurrence. Null if no day is selected.
    /// </summary>
    public static DateTimeOffset? NextFire(Schedule s, DateTimeOffset now, TimeZoneInfo tz, SunLocation? loc = null)
    {
        if ((s.Days & EveryDay) == 0) return null;
        bool sun = s.Trigger is "sunrise" or "sunset";
        if (sun && loc == null) return null; // needs coordinates in settings
        var today = TimeZoneInfo.ConvertTime(now, tz).Date;
        for (int i = -1; i <= (sun ? 400 : 8); i++) // near the poles the sun may not rise or set for months
        {
            var day = today.AddDays(i);
            if (!sun && (s.Days & DayBit(day.DayOfWeek)) == 0) continue;
            DateTimeOffset t;
            if (sun) { if (Sun.Event(DateOnly.FromDateTime(day), loc!, s.Trigger == "sunrise", tz) is not { } e) continue; t = e.AddMinutes(s.Offset); }
            else t = Resolve(day.AddHours(s.Hour).AddMinutes(s.Minute), tz);
            // Days apply to the day it actually runs: a large offset can move a sun run across midnight.
            if (sun && (s.Days & DayBit(TimeZoneInfo.ConvertTime(t, tz).DayOfWeek)) == 0) continue;
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

    /// <summary>Clock set back: a due time at or before the last one that already fired must not run again. 0 = never fired.</summary>
    public static bool IsReplay(DateTimeOffset due, long lastFiredMs) => lastFiredMs > 0 && due.ToUnixTimeMilliseconds() <= lastFiredMs;

    /// <summary>Next run strictly after both <paramref name="now"/> and the last fired time.</summary>
    public static DateTimeOffset? NextFireAfter(Schedule s, DateTimeOffset now, TimeZoneInfo tz, long lastFiredMs, SunLocation? loc = null)
    {
        var last = DateTimeOffset.FromUnixTimeMilliseconds(Math.Max(0, lastFiredMs));
        return NextFire(s, lastFiredMs > 0 && last > now ? last : now, tz, loc);
    }

    /// <summary>Null when the entry is usable, else why not.</summary>
    public static string? InvalidReason(Schedule s) =>
        s.Id == 0 ? "missing id"
        : s.Hour is < 0 or > 23 ? $"hour {s.Hour}"
        : s.Minute is < 0 or > 59 ? $"minute {s.Minute}"
        : s.Days is < 0 or > EveryDay ? $"days {s.Days}"
        : !ValidAction(s.Action) ? $"action {s.Action}"
        : !Triggers.Contains(s.Trigger) ? $"trigger {s.Trigger}"
        : s.Offset is < -MaxOffset or > MaxOffset ? $"offset {s.Offset}"
        : !(s.Target == "all" || System.Text.RegularExpressions.Regex.IsMatch(s.Target ?? "", @"^(room|bulb):\d{1,9}$")) ? $"target {s.Target}"
        : null;

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

    public static string ActionText(string a) => a switch
    {
        "on" => "Turn on", "off" => "Turn off",
        var x when x.StartsWith("effect:") => int.TryParse(x[7..], out var id) ? (id == 0 ? "Stop effect" : "Effect: " + Effects.Name(id)) : x,
        var x when x.StartsWith("fadein:") => $"Fade in over {FadeMins(x)} min (wake-up)",
        var x when x.StartsWith("fadeout:") => $"Fade out over {FadeMins(x)} min",
        _ => "Scene: " + a,
    };
    public static string WhenText(Schedule s) => s.Trigger is "sunrise" or "sunset"
        ? (s.Trigger == "sunrise" ? "Sunrise" : "Sunset") + (s.Offset > 0 ? $" +{s.Offset} min" : s.Offset < 0 ? $" −{-s.Offset} min" : "")
        : $"{s.Hour:00}:{s.Minute:00}";
    public static string Describe(Home? h, Schedule s) => $"{WhenText(s)} · {DaysText(s.Days)} · {ActionText(s.Action)} · {TargetName(h, s)}";

    /// <summary>Runs the action on the targets over the given router.</summary>
    public static async Task<BatchResult> RunAsync(Router r, Home h, Schedule s, CancellationToken ct = default)
    {
        var bulbs = Targets(h, s).Where(b => b.AddressKnown).ToList();
        if (FadeMins(s.Action) is { } mins) return await Fades.RunAsync(r, h, bulbs, s.Action.StartsWith("fadein:"), mins, ct);
        if (s.Action.StartsWith("effect:"))
        {
            var color = bulbs.Where(Effects.Supports).ToList();
            if (color.Count == 0) return new BatchResult(0, bulbs.Count, "no full-color bulb for effects");
            return await r.SendManyAsync(h, color, new Cmd.Effect(int.Parse(s.Action[7..])), ct);
        }
        if (s.Action == "on" || s.Action == "off") return await r.SendManyAsync(h, bulbs, new Cmd.Power(s.Action == "on"), ct);
        var scene = Scenes.All.FirstOrDefault(x => x.Name == s.Action) ?? throw new InvalidDataException("unknown scene " + s.Action);
        return await Scenes.ApplyAsync(r, h, scene, bulbs);
    }

    static readonly JsonSerializerOptions O = new() { WriteIndented = true };
    public static string ToJson(List<Schedule> l) => JsonSerializer.Serialize(new { Format = 1, Schedules = l }, O);
    public static List<Schedule> FromJson(string json) => Parse(json).Schedules;

    /// <summary>
    /// Like <see cref="ToJson"/>, but entries of <paramref name="existing"/> that were skipped as invalid at load are written back
    /// unchanged (after the valid ones), so saving never silently deletes them. Unreadable <paramref name="existing"/>: plain ToJson.
    /// </summary>
    public static string ToJsonKeepingSkipped(List<Schedule> l, string? existing)
    {
        var keep = new List<System.Text.Json.Nodes.JsonNode>();
        if (existing != null)
            try
            {
                var root = System.Text.Json.Nodes.JsonNode.Parse(existing);
                if (root?["Schedules"] is System.Text.Json.Nodes.JsonArray a)
                {
                    var seen = new HashSet<long>();
                    foreach (var e in a)
                    {
                        Schedule? x = null;
                        try { x = e?.Deserialize<Schedule>(); } catch (Exception) { }
                        if (x != null && InvalidReason(x) == null && seen.Add(x.Id)) continue; // valid: the caller's list is authoritative
                        if (e != null) keep.Add(e.DeepClone());
                    }
                }
            }
            catch (Exception) { }
        if (keep.Count == 0) return ToJson(l);
        var arr = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(l))!.AsArray();
        foreach (var k in keep) arr.Add(k);
        return new System.Text.Json.Nodes.JsonObject { ["Format"] = 1, ["Schedules"] = arr }.ToJsonString(O);
    }

    /// <summary>Throws only when the text is not JSON; bad entries (hour 24, unknown action, ...) are skipped and counted.</summary>
    public static (List<Schedule> Schedules, int Skipped) Parse(string json)
    {
        using var d = JsonDocument.Parse(json);
        if (d.RootElement.ValueKind != JsonValueKind.Object || !d.RootElement.TryGetProperty("Schedules", out var a) || a.ValueKind != JsonValueKind.Array) return ([], 0);
        var l = new List<Schedule>(); int bad = 0;
        foreach (var e in a.EnumerateArray())
        {
            try
            {
                var x = e.Deserialize<Schedule>();
                if (x == null || InvalidReason(x) != null || l.Any(o => o.Id == x.Id)) bad++; else l.Add(x);
            }
            catch (Exception) { bad++; }
        }
        return (l, bad);
    }

    /// <summary>
    /// Loads schedules.json. An unreadable file is renamed to "&lt;name&gt;.corrupt-&lt;stamp&gt;" and an empty list returned,
    /// so the next save never overwrites it.
    /// </summary>
    public static (List<Schedule> Schedules, int Skipped, string? Quarantined) LoadOrQuarantine(string path, string stamp)
    {
        if (!File.Exists(path)) return ([], 0, null);
        try { var (l, bad) = Parse(File.ReadAllText(path)); return (l, bad, null); }
        catch (Exception)
        {
            var q = path + ".corrupt-" + stamp;
            try { File.Move(path, q); } catch (Exception) { q = path; }
            return ([], 0, Path.GetFileName(q));
        }
    }
}
