
namespace Lanternly.Core;

public abstract record Cmd
{
    public sealed record Power(bool On) : Cmd;
    public sealed record Brightness(int Pct) : Cmd;
    public sealed record Temp(int Ct) : Cmd;          // 1 warm .. 100 cool (0 is invalid)
    public sealed record Rgb(byte R, byte G, byte B) : Cmd;
    public sealed record Identify(bool Start) : Cmd;  // blink to find a bulb; no state change
}

/// <summary>A way to reach bulbs. Lanternly is offline-only: the one implementation is the Bluetooth mesh (plus test fakes).</summary>
public interface ILink
{
    Via Kind { get; }
    bool CanReach(Home h, Bulb b);
    Task SendAsync(Home h, Bulb b, Cmd c, CancellationToken ct = default);
    event Action<Home?, BulbState>? StateChanged;
}

/// <summary>Picks the first link that can reach a bulb.</summary>
public sealed class Router(IEnumerable<ILink> links)
{
    readonly List<ILink> _links = links.ToList();
    public IReadOnlyList<ILink> Links => _links;

    public ILink? Pick(Home h, Bulb b) => _links.FirstOrDefault(l => l.CanReach(h, b));

    public async Task<Via> SendAsync(Home h, Bulb b, Cmd c, CancellationToken ct = default)
    {
        Exception? last = null;
        foreach (var l in _links.Where(l => l.CanReach(h, b)))
        {
            try { await l.SendAsync(h, b, c, ct); b.Via = l.Kind; return l.Kind; }
            catch (Exception ex) when (ex is not OperationCanceledException) { last = ex; }
        }
        b.Via = Via.None;
        throw last ?? new InvalidOperationException($"{b.Name} is not reachable (no LAN bridge, BLE or cloud link).");
    }

    public Task<BatchResult> SendRoomAsync(Home h, Room r, Cmd c, CancellationToken ct = default)
        => SendManyAsync(h, h.Bulbs.Where(b => r.MeshIds.Contains(b.MeshId)), c, ct);

    /// <summary>Sends to each bulb; keeps going after failures and reports how many succeeded.</summary>
    public async Task<BatchResult> SendManyAsync(Home h, IEnumerable<Bulb> bulbs, Cmd c, CancellationToken ct = default)
    {
        int ok = 0, total = 0; string? first = null;
        foreach (var b in bulbs)
        {
            total++;
            try { await SendAsync(h, b, c, ct); ok++; }
            catch (Exception ex) when (ex is not OperationCanceledException) { first ??= $"{b.Name}: {ex.Message}"; Diag.Log("send " + c.GetType().Name, ex); }
        }
        return new BatchResult(ok, total, first);
    }
}

/// <summary>Diagnostics sink. Apps route this to their log. Callers pass exception types only, never credentials, tokens or keys.</summary>
public static class Diag
{
    public static Action<string, Exception?>? Sink;
    public static void Log(string where, Exception? ex = null) { try { Sink?.Invoke(where, ex); } catch { /* logging must never throw */ } }
}

/// <summary>Result of a multi-bulb operation, e.g. "3/5 bulbs updated".</summary>
public sealed record BatchResult(int Ok, int Total, string? FirstError)
{
    public bool AllOk => Ok == Total;
    public override string ToString() => AllOk ? $"{Total}/{Total} bulbs updated" : $"{Ok}/{Total} bulbs updated" + (FirstError is null ? "" : $" ({FirstError})");
}

/// <summary>Minimal GATT surface so the Telink session can run on WinRT, Android or a test fake.</summary>
public interface IGatt : IAsyncDisposable
{
    string Mac { get; }
    Task WriteAsync(Guid ch, byte[] data, bool withResponse);
    Task<byte[]> ReadAsync(Guid ch);
    Task SubscribeAsync(Guid ch, Action<byte[]> onNotify);
}

/// <summary>Logged-in Telink mesh session through one bulb; reaches every node of that mesh.</summary>
public sealed class BleSession : ILink, IAsyncDisposable
{
    readonly IGatt _g; readonly Home _home; byte[]? _sk; readonly byte[] _mac; int _seq = Random.Shared.Next(1, 0xffff);
    public Via Kind => Via.Ble;
    public event Action<Home?, BulbState>? StateChanged;
    public bool LoggedIn => _sk != null;

    public BleSession(IGatt gatt, Home home) { _g = gatt; _home = home; _mac = Ble.MacLe(gatt.Mac); }

    public async Task LoginAsync()
    {
        var rand = System.Security.Cryptography.RandomNumberGenerator.GetBytes(8);
        await _g.WriteAsync(Ble.PairChar, Ble.PairRequest(_home.MeshName, _home.MeshPassword, rand), true);
        await Task.Delay(300);
        var reply = await _g.ReadAsync(Ble.PairChar);
        _sk = Ble.SessionKey(_home.MeshName, _home.MeshPassword, rand, reply);
        await _g.SubscribeAsync(Ble.NotifyChar, d => { foreach (var s in Ble.ParseStatus(Ble.DecryptNotify(_sk!, _mac, d))) StateChanged?.Invoke(_home, s); });
        await _g.WriteAsync(Ble.NotifyChar, [1], true); // ask nodes to report status
    }

    public bool CanReach(Home h, Bulb b) => LoggedIn && h == _home;
    int NextSeq() { while (true) { var v = Interlocked.Increment(ref _seq) & 0xffffff; if (v != 0) return v; } }

    /// <summary>
    /// Moves a factory-reset bulb (logged in with telink_mesh1/123) onto the given mesh. Experimental:
    /// follows the published Telink/Cync provisioning flow; confirmed only against a simulated node.
    /// </summary>
    public static async Task ProvisionAsync(IGatt g, string meshName, string meshPassword)
    {
        var rand = System.Security.Cryptography.RandomNumberGenerator.GetBytes(8);
        await g.WriteAsync(Ble.PairChar, Ble.PairRequest(Ble.FactoryName, Ble.FactoryPassword, rand), true);
        await Task.Delay(300);
        var sk = Ble.SessionKey(Ble.FactoryName, Ble.FactoryPassword, rand, await g.ReadAsync(Ble.PairChar));
        var ltk = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        foreach (var w in Ble.ProvisionWrites(sk, meshName, meshPassword, ltk)) await g.WriteAsync(Ble.PairChar, w, true);
        await Task.Delay(300);
        var r = await g.ReadAsync(Ble.PairChar);
        if (r.Length == 0 || r[0] != Ble.PairConfirmed) throw new IOException("bulb did not confirm the new mesh key");
    }

    public Task SendAsync(Home h, Bulb b, Cmd c, CancellationToken ct = default)
        => Send(b.MeshId, c);

    public Task Send(ushort target, Cmd c)
    {
        var (cmd, data) = c switch
        {
            Cmd.Power p => (Ble.CmdPower, Ble.PowerData(p.On)),
            Cmd.Brightness x => (Ble.CmdBrightness, Ble.BrightnessData(x.Pct)),
            Cmd.Temp t => (Ble.CmdColor, Ble.ColorTempData(t.Ct)),
            Cmd.Rgb r => (Ble.CmdColor, Ble.RgbData(r.R, r.G, r.B)),
            Cmd.Identify i => (Ble.CmdIdentify, Ble.IdentifyData(i.Start)),
            _ => throw new ArgumentException("unknown command"),
        };
        var pkt = Ble.Command(_sk ?? throw new InvalidOperationException("BLE not logged in"), _mac, NextSeq(), target, cmd, data);
        return _g.WriteAsync(Ble.CommandChar, pkt, false);
    }

    public ValueTask DisposeAsync() { _sk = null; return _g.DisposeAsync(); }
}
