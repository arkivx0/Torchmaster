namespace Lanternly.Core;

/// <summary>UI color tokens and bulb swatches, mirrored from spec/theme_tokens.json (TokensTests fail on drift). Used by Theme.cs.</summary>
public static class Tokens
{
    public static readonly IReadOnlyDictionary<string, string> Colors = new Dictionary<string, string>
    {
        ["bg"] = "#1A1B26", ["card"] = "#24283B", ["ctl"] = "#2F3549", ["ctlHover"] = "#3B4261", ["line"] = "#565F89",
        ["fg"] = "#C0CAF5", ["dim"] = "#9AA5CE", ["accent"] = "#F25C54", ["onAccent"] = "#1A1B26",
        ["warn"] = "#E0AF68", ["onWarn"] = "#1A1B26", ["ok"] = "#9ECE6A", ["focus"] = "#7AA2F7",
    };
    /// <summary>Named bulb colors (kept separate from the UI colors).</summary>
    public static readonly (string Name, string Hex)[] Swatches =
        [("Red", "#F7768E"), ("Orange", "#FF9E64"), ("Amber", "#E0AF68"), ("Green", "#9ECE6A"), ("Teal", "#73DACA"), ("Blue", "#7AA2F7"), ("Violet", "#BB9AF7")];
    public const int MinClickPx = 32, MinTouchPx = 44;
}
