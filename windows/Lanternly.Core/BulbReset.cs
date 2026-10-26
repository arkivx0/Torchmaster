namespace Lanternly.Core;

/// <summary>
/// "Reset bulb": sends the Telink kick-out (0xE3, data 01) so a bulb whose mesh credentials we know erases its pairing
/// and returns to factory defaults (telink_mesh1 / 123). The bulb does not acknowledge, so the app then looks for its
/// factory-reset advertisement and only forgets it (with its single-bulb schedules) when seen or when the user says so.
/// </summary>
public static class BulbReset
{
    public const string ConfirmTitle = "Reset bulb?";
    /// <summary>Unicast node addresses. 0 is "the directly connected node"; 0x8000 and up are group / broadcast (0xFFFF = every bulb).</summary>
    public const int MaxAddress = 0x7FFF;
    public static readonly TimeSpan ConfirmScan = TimeSpan.FromSeconds(10);
    public static bool ValidAddress(int a) => a is >= 1 and <= MaxAddress;

    public static string ConfirmText(Bulb b) =>
        $"Reset \"{b.Name}\" to factory settings?\n\nThis erases the bulb's pairing; you'll need to add it again " +
        "(Settings → Add a reset bulb → Find reset bulbs). It will stop answering to this home until then, and it loses its color and brightness. " +
        "Schedules for this bulb alone are deleted.\n\n" +
        "Keep the bulb powered on and close to this device.";
    public const string Done = "Reset confirmed: the bulb is back to factory settings and was removed from this home, with its own schedules. Open Settings → Add a reset bulb → Find reset bulbs to add it again.";
    public const string UnconfirmedTitle = "Couldn't confirm the reset";
    public static string UnconfirmedText(Bulb b) =>
        $"The reset was sent, but \"{b.Name}\" was not seen restarting as a reset bulb. It may be out of range and still paired.\n\n" +
        "Yes = Keep: leave it in this home (you can try again closer).\nNo = Remove anyway: forget it and its own schedules (if it is still paired, only a power-switch reset brings it back).";

    /// <summary>Why this bulb must not be reset from the app, or null when it can.</summary>
    public static string? RefuseReason(Home h, Bulb b) =>
        !ValidAddress(b.MeshId) ? $"\"{b.Name}\" has mesh address {b.MeshId} (0x{b.MeshId:X}), which is not a single bulb's address (1 to 0x7FFF). Not reset; use the power-switch reset instead."
        : h.Bulbs.Count(x => x.MeshId == b.MeshId) > 1 ? $"\"{b.Name}\" shares mesh address {b.MeshId} with another bulb in this home, so the reset would erase both. Not reset; use the power-switch reset instead."
        : null;

    /// <summary>Sends the reset (two copies). Throws, removing nothing, when refused or when the first copy could not be sent. Does not forget the bulb.</summary>
    public static async Task SendAsync(Router r, Home h, Bulb b, CancellationToken ct = default)
    {
        if (RefuseReason(h, b) is { } why) throw new InvalidOperationException(why);
        await r.SendAsync(h, b, new Cmd.FactoryReset(), ct); // first copy must be accepted
        try { await Task.Delay(300, ct); await r.SendAsync(h, b, new Cmd.FactoryReset(), ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { Diag.Log("reset repeat (link already gone is expected)", ex); }
    }

    /// <summary>True when the bulb shows up among the factory-reset (telink_mesh1) advertisers.</summary>
    public static bool Confirmed(Bulb b, IEnumerable<string> factoryMacs) =>
        !string.IsNullOrWhiteSpace(b.Mac) && factoryMacs.Any(m => string.Equals(m, b.Mac, StringComparison.OrdinalIgnoreCase));

    /// <summary>Schedules that control only this bulb: they must go with it, or a later bulb given the same address would inherit them.</summary>
    public static List<Schedule> SchedulesFor(Home h, Bulb b, IEnumerable<Schedule> l) =>
        l.Where(s => s.HomeId == h.Id && s.Target == $"bulb:{b.MeshId}").ToList();

    /// <summary>Removes the bulb from the home and from every room.</summary>
    public static void Forget(Home h, Bulb b)
    {
        h.Bulbs.Remove(b);
        if (h.Bulbs.Any(x => x.MeshId == b.MeshId)) return; // another entry still uses this address
        foreach (var room in h.Rooms) room.MeshIds.RemoveAll(m => m == b.MeshId);
    }

    /// <summary>Key-file import check: every bulb/room address must be 0 (unknown) or 1..0x7FFF, and no two bulbs of a home may share one.</summary>
    public static void CheckImport(Vault v)
    {
        foreach (var h in v.Homes)
        {
            if (h.Bulbs.FirstOrDefault(b => b.MeshId > MaxAddress) is { } bad) throw new InvalidDataException($"bulb \"{bad.Name}\" has invalid mesh address {bad.MeshId}");
            if (h.Rooms.FirstOrDefault(r => r.MeshIds.Any(m => m > MaxAddress)) is { } br) throw new InvalidDataException($"room \"{br.Name}\" has an invalid mesh address");
            if (h.Bulbs.Where(b => b.MeshId != 0).GroupBy(b => b.MeshId).FirstOrDefault(g => g.Count() > 1) is { } d)
                throw new InvalidDataException($"bulbs \"{d.First().Name}\" and \"{d.Skip(1).First().Name}\" share mesh address {d.Key}");
        }
    }
}
