using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lanternly.Core;
namespace Lanternly.Win;

public sealed class Settings
{
    public bool Ble { get; set; } = true;
    public int HomeIndex { get; set; }
    /// <summary>False after bulbs were added locally until a key file is exported (backup nag).</summary>
    public bool BackedUp { get; set; } = true;
}

/// <summary>
/// Vault (mesh keys and device list) is DPAPI-encrypted for the current Windows user.
/// A vault that cannot be read is moved aside to vault.bin.corrupt-&lt;time&gt; and saving is blocked
/// until the user imports a key file or forgets everything, so a bad read never overwrites good keys.
/// </summary>
public static class Store
{
    public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lanternly");
    static string VaultPath => Path.Combine(Dir, "vault.bin");
    static string SettingsPath => Path.Combine(Dir, "settings.json");
    static readonly byte[] Entropy = "Lanternly.vault.v1"u8.ToArray();

    /// <summary>Set when the vault failed to load: path of the quarantined copy. Saves are refused while set.</summary>
    public static string? Quarantined { get; private set; }

    public static Vault LoadVault()
    {
        if (!File.Exists(VaultPath)) return new();
        try
        {
            var json = Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(VaultPath), Entropy, DataProtectionScope.CurrentUser));
            var v = Vault.FromJson(json);
            if (json.Contains("\"Cloud\"")) try { SaveVault(v); } catch (Exception ex) { Program.Log(ex, "vault rewrite"); } // 0.1.0 vault with a cloud token: rewrite without it
            return v;
        }
        catch (Exception ex)
        {
            Program.Log(ex, "vault load");
            var q = $"{VaultPath}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            try { File.Move(VaultPath, q); } catch (Exception mv) { Program.Log(mv, "vault quarantine"); q = VaultPath; }
            Quarantined = q;
            return new();
        }
    }

    /// <summary>Clears the save block; call only after the user imported a key file or chose Forget.</summary>
    public static void Unblock() => Quarantined = null;

    public static void SaveVault(Vault v)
    {
        if (Quarantined != null) throw new InvalidOperationException("Saving is blocked because the stored keys could not be read. Import a key file or use Forget everything.");
        Directory.CreateDirectory(Dir);
        File.WriteAllBytes(VaultPath + ".tmp", ProtectedData.Protect(Encoding.UTF8.GetBytes(v.ToJson()), Entropy, DataProtectionScope.CurrentUser));
        File.Move(VaultPath + ".tmp", VaultPath, true);
    }

    public static Settings LoadSettings()
    {
        try { return File.Exists(SettingsPath) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath)) ?? new() : new(); }
        catch (Exception ex) { Program.Log(ex, "settings load"); return new(); }
    }
    public static void SaveSettings(Settings s) { Directory.CreateDirectory(Dir); File.WriteAllText(SettingsPath, JsonSerializer.Serialize(s)); }

    public static void Forget()
    {
        foreach (var f in new[] { VaultPath, SettingsPath, Path.Combine(Dir, "bridge-cert.bin") /* 0.1.x leftovers */ })
            try { File.Delete(f); } catch (Exception ex) { Program.Log(ex, "forget"); }
        Quarantined = null;
    }
}
