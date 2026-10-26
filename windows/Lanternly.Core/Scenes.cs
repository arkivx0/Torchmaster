namespace Lanternly.Core;

/// <summary>Quick scenes. Commands are applied per bulb; color steps are skipped on bulbs without that capability.</summary>
public static class Scenes
{
    public sealed record Scene(string Name, Cmd[] Steps);

    public static readonly Scene[] All =
    [
        new("Bright", [new Cmd.Power(true), new Cmd.Brightness(100), new Cmd.Temp(70)]),
        new("Relax", [new Cmd.Power(true), new Cmd.Brightness(45), new Cmd.Temp(10)]),
        new("Focus", [new Cmd.Power(true), new Cmd.Brightness(90), new Cmd.Temp(100)]),
        new("Night", [new Cmd.Power(true), new Cmd.Brightness(8), new Cmd.Temp(1)]),
        new("Red", [new Cmd.Power(true), new Cmd.Brightness(100), new Cmd.Rgb(255, 0, 0)]),
        new("Off", [new Cmd.Power(false)]),
    ];

    public static IEnumerable<Cmd> For(Scene s, Caps c) => s.Steps.Where(x => x switch
    {
        Cmd.Brightness => c.HasFlag(Caps.Dim),
        Cmd.Temp => c.HasFlag(Caps.Temp),
        Cmd.Rgb or Cmd.Effect => c.HasFlag(Caps.Color),
        _ => true,
    });

    /// <summary>Applies a scene; a bulb counts as updated only if every step reached it.</summary>
    public static async Task<BatchResult> ApplyAsync(Router r, Home h, Scene s, IEnumerable<Bulb>? only = null)
    {
        int ok = 0, total = 0; string? first = null;
        foreach (var b in only ?? h.Bulbs)
        {
            total++;
            try { foreach (var c in For(s, b.Caps)) await r.SendAsync(h, b, c); ok++; }
            catch (Exception ex) when (ex is not OperationCanceledException) { first ??= $"{b.Name}: {ex.Message}"; Diag.Log("scene", ex); }
        }
        return new BatchResult(ok, total, first);
    }
}
