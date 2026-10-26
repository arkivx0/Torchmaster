using System.Text;
using Lanternly.Core;
using Xunit;

namespace Lanternly.Core.Tests;

/// <summary>Mock Cync cloud TCP server: login ack, probe replies, records pipe frames, answers status queries.</summary>
sealed class FakeNode(string mac, string name, string pass) : IGatt
{
    public string Mac => mac;
    byte[]? _sk; byte[] _pairReply = [];
    public readonly List<byte[]> Plain = [];
    Action<byte[]>? _notify;

    public Task WriteAsync(Guid ch, byte[] d, bool resp)
    {
        if (ch == Ble.PairChar && d[0] is >= 4 and <= 6) { HandleProvision(d); return Task.CompletedTask; }
        if (ch == Ble.PairChar)
        {
            var key = new byte[16]; Array.Copy(d, 1, key, 0, 8);
            var expect = Ble.PairRequest(name, pass, d[1..9]);
            if (!expect.SequenceEqual(d)) { _pairReply = [0x0e, .. new byte[16]]; return Task.CompletedTask; }
            byte[] devRand = [9, 8, 7, 6, 5, 4, 3, 2];
            _pairReply = [0x0d, .. devRand, .. new byte[8]];
            _sk = Ble.SessionKey(name, pass, d[1..9], _pairReply);
        }
        else if (ch == Ble.CommandChar)
        {
            var m = Ble.MacLe(mac); var p = (byte[])d.Clone();
            var iv = new byte[16] { 0, m[0], m[1], m[2], m[3], 1, p[0], p[1], p[2], 0, 0, 0, 0, 0, 0, 0 };
            var ks = Ble.Aes(_sk!, iv); for (int i = 0; i < 15; i++) p[i + 5] ^= ks[i];
            var check = Ble.EncryptPacket(_sk!, m, [.. p[..3], 0, 0, .. p[5..]]);
            Assert.Equal(d[3..5], check[3..5]); // MIC verifies
            Plain.Add(p);
            if (p[7] == Ble.CmdKickOut && p[10] == Ble.KickOutFactoryReset) { name = Ble.FactoryName; pass = Ble.FactoryPassword; _sk = null; KickedOut++; }
        }
        else if (ch == Ble.NotifyChar && _notify != null)
        {
            var st = new byte[20]; st[0] = 1; st[7] = Ble.CmdStatus; st[10] = 3; st[11] = 1; st[12] = 25; st[13] = 10;
            var m = Ble.MacLe(mac);
            _notify(Ble.DecryptNotify(_sk!, m, st)); // XOR keystream is symmetric
        }
        return Task.CompletedTask;
    }
    public Task<byte[]> ReadAsync(Guid ch) => Task.FromResult(_pairReply);
    public byte[]? NewName, NewPass;
    public int KickedOut;
    public void HandleProvision(byte[] d)
    {
        // device side: decrypt with the Telink AES inverse and adopt the new credentials
        using var a = System.Security.Cryptography.Aes.Create(); a.Key = _sk!.Reverse().ToArray();
        byte[] Dec(byte[] x) { var o = a.DecryptEcb(x[1..17].Reverse().ToArray(), System.Security.Cryptography.PaddingMode.None); Array.Reverse(o); return o; }
        if (d[0] == Ble.PairName) NewName = Dec(d);
        else if (d[0] == Ble.PairPassword) NewPass = Dec(d);
        else if (d[0] == Ble.PairLtk) { name = System.Text.Encoding.UTF8.GetString(NewName!).TrimEnd('\0'); pass = System.Text.Encoding.UTF8.GetString(NewPass!).TrimEnd('\0'); _pairReply = [Ble.PairConfirmed]; }
    }
    public Task SubscribeAsync(Guid ch, Action<byte[]> n) { _notify = n; return Task.CompletedTask; }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public class EndToEndTests
{
    static Home MakeHome() => new()
    {
        Id = 50, Name = "Test", MeshName = "AABBCCDDEEFF", MeshPassword = "123456",
        Bulbs = [new Bulb { Name = "Lamp", MeshId = 2, Type = 6, Mac = "AA:BB:CC:DD:EE:01" },
                 new Bulb { Name = "Desk", MeshId = 3, Type = 6, Mac = "AA:BB:CC:DD:EE:02" }],
        Rooms = [new Room { Id = 1, Name = "Office", MeshIds = [2, 3] }],
    };

    static async Task Until(Func<bool> f) { for (int i = 0; i < 100 && !f(); i++) await Task.Delay(30); Assert.True(f()); }

    sealed class FlakyLink : ILink
    {
        public Via Kind => Via.Ble;
        public event Action<Home?, BulbState>? StateChanged { add { } remove { } }
        public bool CanReach(Home h, Bulb b) => true;
        public Task SendAsync(Home h, Bulb b, Cmd c, CancellationToken ct = default) => b.Name == "Desk" ? throw new IOException("radio busy") : Task.CompletedTask;
    }

    [Fact]
    public async Task RoomAndSceneReportPartialSuccess()
    {
        var home = MakeHome(); var r = new Router([new FlakyLink()]);
        var res = await r.SendRoomAsync(home, home.Rooms[0], new Cmd.Power(true));
        Assert.Equal("1/2 bulbs updated (Desk: radio busy)", res.ToString());
        var sc = await Scenes.ApplyAsync(r, home, Scenes.All[0]);
        Assert.Equal(1, sc.Ok); Assert.False(sc.AllOk);
    }

    [Fact]
    public async Task BleSessionLogsInSendsAndReadsStatus()
    {
        var home = MakeHome();
        var node = new FakeNode(home.Bulbs[0].Mac, home.MeshName, home.MeshPassword);
        await using var ble = new BleSession(node, home);
        BulbState? got = null; ble.StateChanged += (_, s) => got = s;
        await ble.LoginAsync();
        Assert.Equal(new BulbState(3, true, true, 25, 10, (0, 0, 0)), got);
        var router = new Router([ble]);
        Assert.Equal(Via.Ble, await router.SendAsync(home, home.Bulbs[1], new Cmd.Temp(80)));
        var p = node.Plain.Single();
        Assert.Equal(3, p[5]); Assert.Equal(Ble.CmdColor, p[7]); Assert.Equal(0x11, p[8]); Assert.Equal(0x02, p[9]);
        Assert.Equal(0x05, p[10]); Assert.Equal(80, p[11]);
    }

    sealed class SlowGatt(IGatt inner) : IGatt
    {
        public readonly List<string> Log = []; int _busy, _n;
        public string Mac => inner.Mac;
        public async Task WriteAsync(Guid ch, byte[] data, bool withResponse)
        {
            if (ch != Ble.CommandChar) { await inner.WriteAsync(ch, data, withResponse); return; }
            if (Interlocked.Increment(ref _busy) > 1) Log.Add("overlap");
            var n = ++_n; Log.Add("begin" + n);
            await Task.Delay(n == 1 ? 150 : 1); // first write (the start) is slow
            await inner.WriteAsync(ch, data, withResponse);
            Log.Add("end" + n); Interlocked.Decrement(ref _busy);
        }
        public Task<byte[]> ReadAsync(Guid ch) => inner.ReadAsync(ch);
        public Task SubscribeAsync(Guid ch, Action<byte[]> f) => inner.SubscribeAsync(ch, f);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    sealed class HungGatt(IGatt inner) : IGatt
    {
        public string Mac => inner.Mac;
        public Task WriteAsync(Guid ch, byte[] data, bool withResponse) => ch == Ble.CommandChar ? new TaskCompletionSource().Task : inner.WriteAsync(ch, data, withResponse);
        public Task<byte[]> ReadAsync(Guid ch) => inner.ReadAsync(ch);
        public Task SubscribeAsync(Guid ch, Action<byte[]> f) => inner.SubscribeAsync(ch, f);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    [Fact] // a write that never completes must not block later commands forever
    public async Task HungWriteTimesOutAndReleasesTheLock()
    {
        var home = MakeHome();
        await using var ble = new BleSession(new HungGatt(new FakeNode(home.Bulbs[0].Mac, home.MeshName, home.MeshPassword)), home) { WriteTimeout = TimeSpan.FromMilliseconds(200) };
        await ble.LoginAsync();
        await Assert.ThrowsAsync<TimeoutException>(() => ble.SendAsync(home, home.Bulbs[0], new Cmd.Power(true)));
        await Assert.ThrowsAsync<TimeoutException>(() => ble.SendAsync(home, home.Bulbs[0], new Cmd.Power(false)).WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact] // a quick second tap must not let the stop overtake a slow start write
    public async Task ConcurrentSendsAreSerializedInOrder()
    {
        var home = MakeHome();
        var node = new FakeNode(home.Bulbs[0].Mac, home.MeshName, home.MeshPassword);
        var slow = new SlowGatt(node);
        await using var ble = new BleSession(slow, home);
        await ble.LoginAsync();
        var start = ble.SendAsync(home, home.Bulbs[1], new Cmd.Identify(true));
        var stop = ble.SendAsync(home, home.Bulbs[1], new Cmd.Identify(false));
        await Task.WhenAll(start, stop);
        Assert.Equal(["begin1", "end1", "begin2", "end2"], slow.Log);
        Assert.Equal(0x01, node.Plain[0][11]); Assert.Equal(0x02, node.Plain[1][11]);
    }

    [Fact]
    public async Task IdentifyStartAndStopReachTheNodeAsF7()
    {
        var home = MakeHome();
        var node = new FakeNode(home.Bulbs[0].Mac, home.MeshName, home.MeshPassword);
        await using var ble = new BleSession(node, home);
        await ble.LoginAsync();
        var router = new Router([ble]);
        await router.SendAsync(home, home.Bulbs[1], new Cmd.Identify(true));
        await router.SendAsync(home, home.Bulbs[1], new Cmd.Identify(false));
        Assert.Equal("f7110203010000000000", Convert.ToHexString(node.Plain[0][7..17]).ToLowerInvariant());
        Assert.Equal("f7110203020000000000", Convert.ToHexString(node.Plain[1][7..17]).ToLowerInvariant());
        Assert.Equal(3, node.Plain[0][5]);
    }

    [Fact] // Reset bulb: E3 11 02 01 to the bulb, the node returns to telink_mesh1/123, the app forgets it, and it can be adopted again
    public async Task ResetBulbReturnsItToFactoryAndItCanBeAdoptedAgain()
    {
        var home = MakeHome();
        var target = home.Bulbs[0];
        home.Rooms.Add(new Room { Id = 9, Name = "Test", MeshIds = [target.MeshId] });
        var node = new FakeNode(target.Mac, home.MeshName, home.MeshPassword);
        await using (var ble = new BleSession(node, home))
        {
            await ble.LoginAsync();
            await BulbReset.ResetAsync(new Router([ble]), home, target);
        }
        Assert.Equal($"e31102{Ble.KickOutFactoryReset:x2}000000000000", Convert.ToHexString(node.Plain[0][7..17]).ToLowerInvariant());
        Assert.Equal(target.MeshId, (ushort)(node.Plain[0][5] | node.Plain[0][6] << 8));
        Assert.True(node.KickedOut >= 1);
        Assert.DoesNotContain(target, home.Bulbs);
        Assert.DoesNotContain(target.MeshId, home.Rooms.Single(r => r.Id == 9).MeshIds);
        // old credentials no longer work; factory ones do, and the bulb can be adopted fresh
        await using (var old = new BleSession(node, home)) await Assert.ThrowsAsync<UnauthorizedAccessException>(old.LoginAsync);
        var (n, p) = Ble.NewMeshCredentials();
        await BleSession.ProvisionAsync(node, n, p);
        var fresh = new Home { MeshName = n, MeshPassword = p, Bulbs = [new Bulb { Name = "Again", MeshId = 0, Type = DeviceCaps.AllControls, Mac = node.Mac }] };
        await using var s2 = new BleSession(node, fresh); await s2.LoginAsync();
        Assert.Equal(Via.Ble, await new Router([s2]).SendAsync(fresh, fresh.Bulbs[0], new Cmd.Power(true)));
    }

    [Fact] // a reset that never left the radio must not remove the bulb
    public async Task ResetBulbKeepsBulbWhenNotSent()
    {
        var home = MakeHome(); var b = home.Bulbs[0];
        await Assert.ThrowsAnyAsync<Exception>(() => BulbReset.ResetAsync(new Router([]), home, b));
        Assert.Contains(b, home.Bulbs);
    }

    [Fact]
    public async Task AdoptFactoryResetBulbOntoOwnMesh()
    {
        var node = new FakeNode("AA:BB:CC:DD:EE:09", Ble.FactoryName, Ble.FactoryPassword);
        var (n, p) = Ble.NewMeshCredentials();
        Assert.True(n.Length <= 16 && p.Length == 16);
        await BleSession.ProvisionAsync(node, n, p);
        // the node now only accepts our new credentials
        var home = new Home { MeshName = n, MeshPassword = p, Bulbs = [new Bulb { Name = "Red lamp", MeshId = 0, Type = DeviceCaps.AllControls, Mac = node.Mac }] };
        await using var s = new BleSession(node, home);
        await s.LoginAsync();
        Assert.Equal(Via.Ble, await new Router([s]).SendAsync(home, home.Bulbs[0], new Cmd.Power(true)));
        Assert.True(home.Bulbs[0].Caps.HasFlag(Caps.Color));
    }

    [Fact]
    public async Task BleWrongMeshPasswordFails()
    {
        var home = MakeHome();
        var node = new FakeNode(home.Bulbs[0].Mac, home.MeshName, "other");
        await using var ble = new BleSession(node, home);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(ble.LoginAsync);
    }

    [Fact]
    public async Task RouterUsesBleAndFailsClearlyWhenNothingReaches()
    {
        var home = MakeHome();
        var node = new FakeNode(home.Bulbs[0].Mac, home.MeshName, home.MeshPassword);
        await using var ble = new BleSession(node, home); await ble.LoginAsync();
        var r = new Router([ble]);
        Assert.Equal(Via.Ble, await r.SendAsync(home, home.Bulbs[0], new Cmd.Power(false)));
        Assert.Equal(Via.Ble, home.Bulbs[0].Via);
        var none = new Router([]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => none.SendAsync(home, home.Bulbs[0], new Cmd.Power(true)));
    }
}
