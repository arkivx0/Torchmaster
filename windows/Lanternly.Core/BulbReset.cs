namespace Lanternly.Core;

/// <summary>
/// "Reset bulb": sends the Telink kick-out (0xE3, data 01) so a bulb whose mesh credentials we know erases its pairing
/// and returns to factory defaults (telink_mesh1 / 123), then forgets it locally so it can be added again.
/// </summary>
public static class BulbReset
{
    public const string ConfirmTitle = "Reset bulb?";
    public static string ConfirmText(Bulb b) =>
        $"Reset \"{b.Name}\" to factory settings?\n\nThis erases the bulb's pairing; you'll need to add it again " +
        "(Settings → Add a reset bulb → Find reset bulbs). It will stop answering to this home until then, and it loses its color and brightness.\n\n" +
        "Keep the bulb powered on and close to this device.";
    public const string Done = "Reset sent. The bulb should blink and restart within a few seconds. It was removed from this home: open Settings → Add a reset bulb → Find reset bulbs to add it again.";

    /// <summary>Sends the reset (a second copy shortly after, as the reset node may drop the link before relaying), then forgets the bulb.</summary>
    public static async Task ResetAsync(Router r, Home h, Bulb b, CancellationToken ct = default)
    {
        await r.SendAsync(h, b, new Cmd.FactoryReset(), ct); // first copy must be accepted
        try { await Task.Delay(300, ct); await r.SendAsync(h, b, new Cmd.FactoryReset(), ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { Diag.Log("reset repeat (link already gone is expected)", ex); }
        Forget(h, b);
    }

    /// <summary>Removes the bulb from the home and from every room.</summary>
    public static void Forget(Home h, Bulb b)
    {
        h.Bulbs.Remove(b);
        if (h.Bulbs.Any(x => x.MeshId == b.MeshId)) return; // another entry still uses this address
        foreach (var room in h.Rooms) room.MeshIds.RemoveAll(m => m == b.MeshId);
    }
}
