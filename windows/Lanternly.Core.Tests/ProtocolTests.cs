using System.Text.Json;
using Lanternly.Core;
using Xunit;

namespace Lanternly.Core.Tests;

/// <summary>Byte-exact checks against vectors produced by the reference Python implementations (spec/test_vectors.json).</summary>
public class ProtocolTests
{
    static readonly Dictionary<string, string> V = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText("test_vectors.json"))!;
    static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    [Fact] public void BleKeyEncrypt() => Assert.Equal(V["ble_key_encrypt"][..16], Hex(Ble.PairRequest("AABBCCDDEEFF", "12345678", [1, 2, 3, 4, 5, 6, 7, 8]).AsSpan(9).ToArray()));

    [Fact] // Published constant from the Cync app (Telink.f28878l): factory pairing write with R_app = A0..A7.
    public void BleFactoryPairingWriteMatchesPublishedConstant()
        => Assert.Equal("0ca0a1a2a3a4a5a6a78db674711b855a79", Hex(Ble.PairRequest("telink_mesh1", "123", [0xa0, 0xa1, 0xa2, 0xa3, 0xa4, 0xa5, 0xa6, 0xa7])));

    [Fact]
    public void BleSessionKey()
    {
        byte[] reply = [0x0d, 0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27];
        Assert.Equal(V["ble_sk"], Hex(Ble.SessionKey("AABBCCDDEEFF", "12345678", [1, 2, 3, 4, 5, 6, 7, 8], reply)));
        Assert.Throws<UnauthorizedAccessException>(() => Ble.SessionKey("a", "b", new byte[8], [0x0e, 0, 0, 0, 0, 0, 0, 0, 0]));
    }

    [Fact]
    public void BleEncryptMatchesReference()
    {
        var sk = Convert.FromHexString(V["ble_sk"]);
        byte[] mac = [0xff, 0xee, 0xdd, 0xcc, 0xbb, 0xaa];
        Assert.Equal(V["ble_enc"], Hex(Ble.EncryptPacket(sk, mac, Convert.FromHexString(V["ble_plain"]))));
        Assert.Equal(V["ble_enc"], Hex(Ble.Command(sk, mac, 0x1234, 5, Ble.CmdBrightness, Ble.BrightnessData(60))));
        Assert.Equal(V["ble_dec_of_enc"], Hex(Ble.DecryptNotify(sk, mac, Convert.FromHexString(V["ble_enc"]))));
        Assert.Equal(mac, Ble.MacLe("AA:BB:CC:DD:EE:FF"));
    }

    [Fact]
    public void BleStatusParse()
    {
        var d = new byte[20]; d[7] = 0xdc;
        d[10] = 3; d[11] = 1; d[12] = 40; d[13] = 70;               // CT mode
        d[14] = 4; d[15] = 1; d[16] = 128 + 50; d[17] = 0b111_000_11; // RGB mode
        var s = Ble.ParseStatus(d);
        Assert.Equal(new BulbState(3, true, true, 40, 70, (0, 0, 0)), s[0]);
        Assert.Equal(new BulbState(4, true, true, 50, 0xfe, (255, 0, 255)), s[1]);
    }

    [Fact]
    public void CapsTable()
    {
        Assert.True(DeviceCaps.Count > 100);
        Assert.Equal(Caps.OnOff | Caps.Dim | Caps.Temp | Caps.Color, DeviceCaps.For(6) & ~Caps.Hub);
        Assert.Equal(Caps.OnOff | Caps.Dim, DeviceCaps.For(99999));
    }

    [Fact]
    public void VaultRoundTripAndOldCloudFieldsAreDropped()
    {
        var v = new Vault { Homes = [new Home { Id = 7, Name = "H", MeshName = "M", MeshPassword = "P", Bulbs = [new Bulb { Name = "B", MeshId = 2, Type = 6 }], Rooms = [new Room { Id = 1, Name = "R", MeshIds = [2] }] }] };
        var v2 = Vault.FromJson(v.ToJson());
        Assert.Equal("P", v2.Homes[0].MeshPassword); Assert.Equal((ushort)2, v2.Homes[0].Rooms[0].MeshIds[0]);
        // A 0.1.0 file carrying a cloud token and device ids still imports; those fields are discarded.
        var old = Vault.FromJson("""{"Format":1,"Homes":[{"Id":1,"MeshName":"m","MeshPassword":"p","Bulbs":[{"Name":"x","DeviceId":5,"MeshId":2}]}],"Cloud":{"AccessToken":"secret"}}""");
        Assert.Equal("p", old.Homes[0].MeshPassword);
        Assert.DoesNotContain("secret", old.ToJson());
    }
}

public class SceneTests
{
    [Fact]
    public void RedSceneIsPowerOnPlusRedAndOnlyColorBulbsGetTheColor()
    {
        var red = Scenes.All.First(s => s.Name == "Red");
        Assert.Equal([new Cmd.Power(true), new Cmd.Rgb(255, 0, 0)], red.Steps);
        Assert.Equal(2, Scenes.For(red, Caps.OnOff | Caps.Dim | Caps.Color).Count());
        Assert.Equal([new Cmd.Power(true)], Scenes.For(red, Caps.OnOff | Caps.Dim | Caps.Temp));
    }

    [Fact] // Identify / blink, as in the official app: F7 11 02 03 01 (start) / 03 02 (stop).
    public void IdentifyCommandLayout()
    {
        Assert.Equal(0xf7, Ble.CmdIdentify);
        Assert.Equal(new byte[] { 0x03, 0x01 }, Ble.IdentifyData(true));
        Assert.Equal(new byte[] { 0x03, 0x02 }, Ble.IdentifyData(false));
        var sk = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray(); var mac = Ble.MacLe("AA:BB:CC:DD:EE:FF");
        foreach (var (start, last) in new[] { (true, (byte)1), (false, (byte)2) })
        {
            var plain = new byte[20] { 0x34, 0x12, 0x00, 0, 0, 0x05, 0x00, 0xf7, 0x11, 0x02, 0x03, last, 0, 0, 0, 0, 0, 0, 0, 0 };
            Assert.Equal(Ble.EncryptPacket(sk, mac, plain), Ble.Command(sk, mac, 0x1234, 5, Ble.CmdIdentify, Ble.IdentifyData(start)));
        }
    }

    [Fact]
    public void ScenesSkipUnsupportedSteps()
    {
        var night = Scenes.All.First(s => s.Name == "Night");
        Assert.Equal(3, Scenes.For(night, Caps.OnOff | Caps.Dim | Caps.Temp).Count());
        Assert.Equal(2, Scenes.For(night, Caps.OnOff | Caps.Dim).Count());
        Assert.Single(Scenes.For(night, Caps.OnOff));
    }

    [Fact]
    public void KeyFileReadsReferenceVector() // spec/keyfile_vector.json is made by an independent Python implementation
    {
        var v = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText("keyfile_vector.json"))!;
        Assert.Equal(v["mesh_password"], KeyFile.Import(v["file"], v["passphrase"]).Homes[0].MeshPassword);
    }

    [Fact]
    public void KeyFileRoundTripsWithAndWithoutPassphrase()
    {
        var v = new Vault { Homes = [new Home { Id = 7, Name = "H", MeshName = "m", MeshPassword = "secret-pw", Bulbs = [new Bulb { Name = "B", MeshId = 2 }] }] };
        var prot = KeyFile.Export(v, "correct horse");
        Assert.True(KeyFile.IsProtected(prot)); Assert.DoesNotContain("secret-pw", prot);
        Assert.Equal("secret-pw", KeyFile.Import(prot, "correct horse").Homes[0].MeshPassword);
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => KeyFile.Import(prot, "wrong"));
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => KeyFile.Import(prot, null));
        var plain = KeyFile.Export(v, null);
        Assert.False(KeyFile.IsProtected(plain)); Assert.Equal("secret-pw", KeyFile.Import(plain, null).Homes[0].MeshPassword);
    }
}

/// <summary>
/// Offline guarantee: no GE cloud host name, and no general network API, anywhere in the shipped source
/// of either app. Fails the build if one creeps back in.
/// </summary>
public class OfflineTests
{
    static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "spec", "PROTOCOL.md"))) d = d.Parent;
        return d?.FullName ?? throw new DirectoryNotFoundException("repo root");
    }

    static IEnumerable<string> SourceFiles(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs") || f.EndsWith(".kt") || f.EndsWith(".kts") || f.EndsWith(".xml") || f.EndsWith(".csproj") || f.EndsWith(".props") || f.EndsWith(".pro"))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}build{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}.gradle{Path.DirectorySeparatorChar}"))
            .Where(f => !f.EndsWith("ProtocolTests.cs") && !f.EndsWith("OfflineTest.kt")); // the checks themselves

    // Built from pieces so this file does not match its own scan.
    static readonly string[] Hosts = ["geli" + "ghting", "xli" + "nk", "cm-" + "sec", "cync" + ".com", "api." + "ge", "cync" + "smart", "savant" + "cloud", "savant" + " cloud", "savant" + ".com"];
    static readonly string[] NetApis = ["Tcp" + "Client", "Tcp" + "Listener", "Http" + "Client", "Ssl" + "Stream", "System.Net." + "Http", "System.Net." + "Sockets", "java.net." + "Socket", "javax.net", "Http" + "URLConnection", "Server" + "Socket", "android.permission." + "INTERNET", "Udp" + "Client", "Datagram" + "Socket", "Web" + "Client", "Web" + "Request"];
    // Vendor LAN/cloud protocol markers (UDP port and Xlink framing).
    static readonly string[] LanMarkers = ["59" + "87", "XLINK" + "_", "Xlink" + "Sdk"];

    [Fact]
    public void NoGeHostNamesInSource()
    {
        var hits = SourceFiles(RepoRoot()).SelectMany(f => Hosts.Where(h => File.ReadAllText(f).Contains(h, StringComparison.OrdinalIgnoreCase)).Select(h => $"{Path.GetFileName(f)}: {h}")).ToList();
        Assert.Empty(hits);
    }

    [Fact]
    public void NoNetworkApisInSource()
    {
        var hits = SourceFiles(RepoRoot()).SelectMany(f => NetApis.Where(h => File.ReadAllText(f).Contains(h)).Select(h => $"{Path.GetFileName(f)}: {h}")).ToList();
        Assert.Empty(hits);
    }

    [Fact]
    public void NoVendorLanMarkersInSource()
    {
        var hits = SourceFiles(RepoRoot()).SelectMany(f => LanMarkers.Where(h => File.ReadAllText(f).Contains(h, StringComparison.OrdinalIgnoreCase)).Select(h => $"{Path.GetFileName(f)}: {h}")).ToList();
        Assert.Empty(hits);
    }

    [Fact]
    public void CoreSourceUsesNoSystemNet()
    {
        var core = Path.Combine(RepoRoot(), "windows", "Lanternly.Core");
        var hits = SourceFiles(core).Where(f => File.ReadAllText(f).Contains("System." + "Net")).Select(Path.GetFileName).ToList();
        Assert.Empty(hits);
    }

    [Fact]
    public void AndroidManifestHasNoInternetPermission()
    {
        var m = File.ReadAllText(Path.Combine(RepoRoot(), "android", "app", "src", "main", "AndroidManifest.xml"));
        Assert.DoesNotContain("permission." + "INTERNET", m);
        Assert.DoesNotContain("ACCESS_" + "NETWORK_STATE", m);
    }

    [Fact]
    public void CoreAssemblyReferencesNoNetworkingAssemblies()
    {
        var refs = typeof(Vault).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();
        Assert.DoesNotContain(refs, n => n.StartsWith("System.Net"));
    }
}
