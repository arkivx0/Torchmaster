using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
namespace Lanternly.Core;

/// <summary>
/// Key-file backup. Plain files are the Vault JSON (homes only). Protected files wrap it with
/// PBKDF2-HMAC-SHA256 (600k iterations, 16-byte salt) and AES-256-GCM (12-byte nonce, 16-byte tag).
/// Same format on Android and Windows; see spec/PROTOCOL.md.
/// </summary>
public static class KeyFile
{
    public const string Magic = "lanternly-keyfile";
    public const int Iterations = 600_000;

    public static string Export(Vault v, string? passphrase)
    {
        var plain = new Vault { Homes = v.Homes }.ToJson();
        if (string.IsNullOrEmpty(passphrase)) return plain;
        var salt = RandomNumberGenerator.GetBytes(16); var nonce = RandomNumberGenerator.GetBytes(12);
        var key = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, Iterations, HashAlgorithmName.SHA256, 32);
        var pt = Encoding.UTF8.GetBytes(plain); var ct = new byte[pt.Length]; var tag = new byte[16];
        using (var g = new AesGcm(key, 16)) g.Encrypt(nonce, pt, ct, tag, Encoding.ASCII.GetBytes(Magic));
        CryptographicOperations.ZeroMemory(key);
        return new JsonObject
        {
            ["format"] = Magic, ["version"] = 1, ["kdf"] = "pbkdf2-sha256", ["iterations"] = Iterations,
            ["salt"] = Convert.ToBase64String(salt), ["nonce"] = Convert.ToBase64String(nonce),
            ["ciphertext"] = Convert.ToBase64String([.. ct, .. tag]),
        }.ToJsonString();
    }

    public static bool IsProtected(string text)
    {
        try { return JsonNode.Parse(text)?["format"]?.GetValue<string>() == Magic; } catch { return false; }
    }

    /// <summary>Reads a plain or protected key file. Wrong passphrase → CryptographicException.</summary>
    public static Vault Import(string text, string? passphrase)
    {
        if (!IsProtected(text)) return Checked(Vault.FromJson(text));
        if (string.IsNullOrEmpty(passphrase)) throw new CryptographicException("This key file is protected with a passphrase.");
        var o = JsonNode.Parse(text)!;
        int it = o["iterations"]!.GetValue<int>();
        if (it < 100_000 || it > 10_000_000) throw new InvalidDataException("bad iteration count");
        var salt = Convert.FromBase64String(o["salt"]!.GetValue<string>()); var nonce = Convert.FromBase64String(o["nonce"]!.GetValue<string>());
        var blob = Convert.FromBase64String(o["ciphertext"]!.GetValue<string>());
        if (blob.Length < 16 || nonce.Length != 12) throw new InvalidDataException("truncated key file");
        var key = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, it, HashAlgorithmName.SHA256, 32);
        var pt = new byte[blob.Length - 16];
        try { using var g = new AesGcm(key, 16); g.Decrypt(nonce, blob.AsSpan(0, pt.Length), blob.AsSpan(pt.Length), pt, Encoding.ASCII.GetBytes(Magic)); }
        catch (AuthenticationTagMismatchException) { throw new CryptographicException("Wrong passphrase, or the file was changed."); }
        finally { CryptographicOperations.ZeroMemory(key); }
        return Checked(Vault.FromJson(Encoding.UTF8.GetString(pt)));
    }

    static Vault Checked(Vault v) { BulbReset.CheckImport(v); return v; }
}
