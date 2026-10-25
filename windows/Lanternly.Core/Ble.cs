using System.Security.Cryptography;
using System.Text;

namespace Lanternly.Core;

/// <summary>
/// Telink BLE mesh crypto and commands used by C by GE / Cync Bluetooth bulbs (spec/PROTOCOL.md §BLE).
/// Login: write 0x0C|rand8|enc8 to the pairing characteristic (…1914), read back 0x0D|devRand8,
/// session key = AES(name^password, rand8|devRand8). Commands: 20-byte packets to …1912.
/// AES here is the Telink variant: key and block byte-reversed.
/// </summary>
public static class Ble
{
    public static readonly Guid Service = Guid.Parse("00010203-0405-0607-0809-0a0b0c0d1910");
    public static readonly Guid NotifyChar = Guid.Parse("00010203-0405-0607-0809-0a0b0c0d1911");
    public static readonly Guid CommandChar = Guid.Parse("00010203-0405-0607-0809-0a0b0c0d1912");
    public static readonly Guid PairChar = Guid.Parse("00010203-0405-0607-0809-0a0b0c0d1914");
    public const ushort Vendor = 0x0211;
    public const byte CmdPower = 0xd0, CmdBrightness = 0xd2, CmdColor = 0xe2, CmdStatus = 0xdc;
    /// <summary>Target address that reaches every node of the mesh.</summary>
    public const ushort Broadcast = 0xffff;

    public static byte[] Aes(byte[] key, byte[] block)
    {
        using var a = System.Security.Cryptography.Aes.Create();
        a.Key = key.Reverse().ToArray();
        var o = a.EncryptEcb(block.Reverse().ToArray(), PaddingMode.None);
        Array.Reverse(o); return o;
    }

    static byte[] Pad16(string s) { var b = new byte[16]; var e = Encoding.UTF8.GetBytes(s); Array.Copy(e, b, Math.Min(16, e.Length)); return b; }
    static byte[] NameXorPass(string name, string pass) { var n = Pad16(name); var p = Pad16(pass); for (int i = 0; i < 16; i++) n[i] ^= p[i]; return n; }

    /// <summary>Pairing request written to …1914. rand8 = 8 random bytes.</summary>
    public static byte[] PairRequest(string name, string pass, byte[] rand8)
    {
        var data = new byte[16]; Array.Copy(rand8, data, 8);
        var enc = Aes(data, NameXorPass(name, pass));
        return [0x0c, .. rand8, .. enc.AsSpan(0, 8)];
    }

    /// <summary>Session key from our rand8 and the device reply (reply[0] must be 0x0D).</summary>
    public static byte[] SessionKey(string name, string pass, byte[] rand8, byte[] reply)
    {
        if (reply.Length < 9 || reply[0] != 0x0d) throw new UnauthorizedAccessException("mesh login rejected (wrong mesh name/password?)");
        return Aes(NameXorPass(name, pass), [.. rand8.AsSpan(0, 8), .. reply.AsSpan(1, 8)]);
    }

    /// <summary>Builds and encrypts one command. mac = device address bytes, little-endian (as in the BLE stack).</summary>
    public static byte[] Command(byte[] sk, byte[] macLe, int seq, ushort target, byte cmd, ReadOnlySpan<byte> data)
    {
        var p = new byte[20];
        p[0] = (byte)seq; p[1] = (byte)(seq >> 8); p[2] = (byte)(seq >> 16);
        p[5] = (byte)target; p[6] = (byte)(target >> 8); p[7] = cmd; p[8] = Vendor & 0xff; p[9] = Vendor >> 8;
        data.CopyTo(p.AsSpan(10));
        return EncryptPacket(sk, macLe, p);
    }

    public static byte[] EncryptPacket(byte[] sk, byte[] mac, byte[] packet)
    {
        var p = (byte[])packet.Clone();
        var nonce = new byte[16] { mac[0], mac[1], mac[2], mac[3], 1, p[0], p[1], p[2], 15, 0, 0, 0, 0, 0, 0, 0 };
        var auth = Aes(sk, nonce);
        for (int i = 0; i < 15; i++) auth[i] ^= p[i + 5];
        var mic = Aes(sk, auth);
        p[3] = mic[0]; p[4] = mic[1];
        var iv = new byte[16] { 0, mac[0], mac[1], mac[2], mac[3], 1, p[0], p[1], p[2], 0, 0, 0, 0, 0, 0, 0 };
        var ks = Aes(sk, iv);
        for (int i = 0; i < 15; i++) p[i + 5] ^= ks[i];
        return p;
    }

    /// <summary>Decrypts a notification from …1911.</summary>
    public static byte[] DecryptNotify(byte[] sk, byte[] mac, byte[] packet)
    {
        var p = (byte[])packet.Clone();
        var iv = new byte[16] { 0, mac[0], mac[1], mac[2], p[0], p[1], p[2], p[3], p[4], 0, 0, 0, 0, 0, 0, 0 };
        var ks = Aes(sk, iv);
        for (int i = 0; i < p.Length - 7; i++) p[i + 7] ^= ks[i];
        return p;
    }

    // ---- provisioning a factory-reset bulb (no cloud) ----
    public const string FactoryName = "telink_mesh1", FactoryPassword = "123";
    public const byte PairName = 4, PairPassword = 5, PairLtk = 6, PairConfirmed = 7;

    /// <summary>The three writes to …1914 that move a logged-in (factory) node onto our own mesh.</summary>
    public static byte[][] ProvisionWrites(byte[] sk, string meshName, string meshPassword, byte[] ltk)
    {
        byte[] W(byte op, byte[] v) { var o = new byte[17]; o[0] = op; Aes(sk, v).AsSpan(0, 16).CopyTo(o.AsSpan(1)); return o; }
        return [W(PairName, Pad16(meshName)), W(PairPassword, Pad16(meshPassword)), W(PairLtk, ltk)];
    }

    /// <summary>Random printable mesh credentials (max 16 bytes each), unique to this user.</summary>
    public static (string Name, string Password) NewMeshCredentials()
    {
        const string a = "abcdefghjkmnpqrstuvwxyz23456789";
        string R(int n) => new(System.Security.Cryptography.RandomNumberGenerator.GetItems<char>(a, n));
        return ("ln" + R(10), R(16));
    }

    public static byte[] PowerData(bool on) => [(byte)(on ? 1 : 0)];
    public static byte[] BrightnessData(int pct) => [(byte)Math.Clamp(pct, 0, 100)];
    public static byte[] ColorTempData(int ct) => [0x05, (byte)Math.Clamp(ct, 1, 100)]; // 0 is not a valid temperature
    public static byte[] RgbData(byte r, byte g, byte b) => [0x04, r, g, b];

    /// <summary>Status notification (decrypted, cmd 0xDC): up to two 4-byte node records at [10..18).</summary>
    public static List<BulbState> ParseStatus(byte[] d)
    {
        var l = new List<BulbState>();
        if (d.Length < 18 || d[7] != CmdStatus) return l;
        for (int o = 10; o < 18; o += 4)
        {
            if (d[o + 1] == 0) continue;
            int bri = d[o + 2]; bool rgb = bri >= 128; if (rgb) bri -= 128;
            var c = d[o + 3];
            (byte, byte, byte) col = rgb ? ((byte)((c >> 5 & 7) * 255 / 7), (byte)((c >> 2 & 7) * 255 / 7), (byte)((c & 3) * 255 / 3)) : ((byte)0, (byte)0, (byte)0);
            l.Add(new BulbState(d[o], true, bri > 0, bri, rgb ? 0xfe : c, col));
        }
        return l;
    }

    /// <summary>"AA:BB:CC:DD:EE:FF" (or no colons) to little-endian bytes.</summary>
    public static byte[] MacLe(string mac)
    {
        var h = mac.Replace(":", "").Replace("-", "");
        if (h.Length != 12) throw new FormatException("bad MAC");
        var b = Convert.FromHexString(h); Array.Reverse(b); return b;
    }
}
