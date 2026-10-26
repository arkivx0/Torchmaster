using System.Text.Json;
using Lanternly.Core;
using Xunit;
namespace Lanternly.Core.Tests;

public class TokensTests
{
    static readonly JsonElement Spec = JsonDocument.Parse(File.ReadAllText("theme_tokens.json")).RootElement;
    [Fact] public void ColorsAndSwatchesMatchSharedFile()
    {
        var c = Spec.GetProperty("colors").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!.ToUpperInvariant());
        Assert.Equal(c.OrderBy(x => x.Key), Tokens.Colors.Select(kv => KeyValuePair.Create(kv.Key, kv.Value.ToUpperInvariant())).OrderBy(x => x.Key));
        var s = Spec.GetProperty("swatches").EnumerateArray().Select(a => (a[0].GetString()!, a[1].GetString()!.ToUpperInvariant())).ToArray();
        Assert.Equal(s, Tokens.Swatches.Select(x => (x.Name, x.Hex.ToUpperInvariant())).ToArray());
    }
    static double Lum(string h) { double Ch(int i) { var v = Convert.ToInt32(h.Substring(i, 2), 16) / 255.0; return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); } return 0.2126 * Ch(1) + 0.7152 * Ch(3) + 0.0722 * Ch(5); }
    static double Contrast(string a, string b) { var x = Lum(a); var y = Lum(b); return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05); }
    [Fact] public void SmallTextContrastAA()
    {
        foreach (var bg in new[] { "bg", "card", "ctl" }) { Assert.True(Contrast(Tokens.Colors["dim"], Tokens.Colors[bg]) >= 4.5); Assert.True(Contrast(Tokens.Colors["fg"], Tokens.Colors[bg]) >= 4.5); }
        Assert.True(Contrast(Tokens.Colors["onWarn"], Tokens.Colors["warn"]) >= 4.5);
    }
}
