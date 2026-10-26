namespace Lanternly.Core;

/// <summary>
/// Light shows. Native shows run inside the bulb: opcode 0xE2, vendor 0x0211, data 07 &lt;mode&gt; &lt;show id&gt; &lt;random&gt;;
/// mode 1 = light show, 0 = static (stop). On the wire: E2 11 02 07 01 &lt;id&gt; &lt;rnd&gt;, as the official app's
/// SetLightRunModeCommand. App-side effects are timed commands sent by Lanternly (see <see cref="AppEffects"/>). Mirrors Effects.kt.
/// </summary>
public static class Effects
{
    public const byte RunMode = 0x07;
    public sealed record Native(string Name, int Id);
    /// <summary>Show ids from the official app's LightRunMode.LightShow table. 65..67 are newer and may not run on older bulbs.</summary>
    public static readonly Native[] All =
    [
        new("Candlelight", 1), new("Rainbow", 2), new("Fireworks", 3), new("Volcanic", 4), new("Aurora", 5), new("Happy Holidays", 6),
        new("Red, White & Blue", 7), new("Vegas", 8), new("Party Time", 9), new("Power Up", 65), new("Splash", 66), new("Cyber", 67),
    ];
    public static Native? Find(int id) => All.FirstOrDefault(e => e.Id == id);
    public static string Name(int id) => id == 0 ? "Stop effect" : Find(id)?.Name ?? $"Effect {id}";
    public static bool Supports(Bulb b) => b.Caps.HasFlag(Caps.Color);
    public static byte[] Data(int id, int? rnd = null) => [RunMode, (byte)(id == 0 ? 0 : 1), (byte)id, (byte)(rnd ?? Random.Shared.Next(256))];
}

/// <summary>App-side effects: Lanternly sends each step while it runs; rate limited.</summary>
public static class AppEffects
{
    public static readonly string[] All = ["Color cycle", "Candle flicker", "Breathe"];
    public const int MaxCmdsPerSec = 4;
    public const int MinStepMs = 400;
    static int BaseStepMs(string n) => n switch { "Color cycle" => 1500, "Candle flicker" => 450, _ => 700 };
    public static int StepMs(string n, int bulbs) => Math.Max(Math.Max(MinStepMs, BaseStepMs(n)), 1000 * Math.Max(1, bulbs) / MaxCmdsPerSec);
    public static bool NeedsColor(string n) => n is "Color cycle" or "Candle flicker";
    public static bool Suits(string n, Bulb b) => b.AddressKnown && (!NeedsColor(n) || b.Caps.HasFlag(Caps.Color)) && (n != "Breathe" || b.Caps.HasFlag(Caps.Dim));

    public static Cmd Step(string n, int i, Random rnd) => n switch
    {
        "Color cycle" => Rgb(Hsv(i * 24 % 360)),
        "Candle flicker" => new Cmd.Rgb(255, (byte)(70 + rnd.Next(50)), (byte)rnd.Next(12)),
        "Breathe" => new Cmd.Brightness(i % 20 < 10 ? 10 + i % 20 * 9 : 100 - (i % 20 - 10) * 9),
        _ => throw new ArgumentException("unknown effect " + n),
    };
    static Cmd Rgb((byte R, byte G, byte B) c) => new Cmd.Rgb(c.R, c.G, c.B);
    public static Cmd? Extra(string n, Random rnd) => n == "Candle flicker" ? new Cmd.Brightness(35 + rnd.Next(30)) : null;

    public static (byte, byte, byte) Hsv(int h)
    {
        var x = (byte)((1 - Math.Abs(h / 60.0 % 2 - 1)) * 255);
        return (h / 60) switch { 0 => (255, x, 0), 1 => (x, 255, 0), 2 => (0, 255, x), 3 => (0, x, 255), 4 => (x, 0, 255), _ => (255, 0, x) };
    }

    /// <summary>Runs until cancelled. Each step goes out before the next delay starts, so a slow link never builds a backlog.</summary>
    public static async Task RunAsync(Router r, Home h, IEnumerable<Bulb> bulbs, string n, CancellationToken ct, Func<int, CancellationToken, Task>? delay = null)
    {
        delay ??= (ms, c) => Task.Delay(ms, c);
        var targets = bulbs.Where(b => Suits(n, b)).ToList();
        var rnd = new Random(); int i = 0; var wait = StepMs(n, targets.Count * (n == "Candle flicker" ? 2 : 1));
        while (!ct.IsCancellationRequested)
        {
            var step = Step(n, i++, rnd);
            foreach (var b in targets)
                try { await r.SendAsync(h, b, step, ct); if (b.Caps.HasFlag(Caps.Dim) && Extra(n, rnd) is { } e) await r.SendAsync(h, b, e, ct); }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { Diag.Log("app effect", ex); }
            try { await delay(wait, ct); } catch (OperationCanceledException) { return; }
        }
    }
}

/// <summary>Fade-in / fade-out over N minutes as timed brightness steps (no long native fade in the bulbs). Mirrors Fades in Effects.kt.</summary>
public static class Fades
{
    public const int MaxMinutes = 60;
    public const long MinStepMs = 30_000;
    public sealed record Step(long AtMs, int Pct);
    public static List<Step> Plan(int from, int to, int minutes)
    {
        int a = Math.Clamp(from, 1, 100), b = Math.Clamp(to, 1, 100); long total = Math.Clamp(minutes, 1, MaxMinutes) * 60_000L;
        int n = Math.Max(1, (int)Math.Min(Math.Abs(b - a), total / MinStepMs));
        var o = new List<Step>();
        for (int k = 0; k <= n; k++) { int p = a + (int)Math.Round((b - a) * k / (double)n, MidpointRounding.AwayFromZero); if (o.Count == 0 || o[^1].Pct != p) o.Add(new(total * k / n, p)); }
        return o;
    }
    public static TimeSpan Duration(int minutes) => TimeSpan.FromMinutes(Math.Clamp(minutes, 1, MaxMinutes));

    /// <summary>Fade in: on at 1 % then up to 100 %. Fade out: from the brightest known level (100 if unknown) to 1 %, then off.</summary>
    public static async Task<BatchResult> RunAsync(Router r, Home h, List<Bulb> bulbs, bool fadeIn, int minutes, CancellationToken ct = default,
        Func<long>? now = null, Func<long, CancellationToken, Task>? delay = null)
    {
        now ??= () => Environment.TickCount64; delay ??= (ms, c) => Task.Delay(TimeSpan.FromMilliseconds(ms), c);
        var dim = bulbs.Where(b => b.Caps.HasFlag(Caps.Dim)).ToList();
        int start = fadeIn ? 1 : Math.Clamp(bulbs.Where(b => b.State is { On: true }).Select(b => b.State!.Brightness).DefaultIfEmpty(100).Max(), 1, 100);
        var steps = Plan(start, fadeIn ? 100 : 1, minutes);
        var reached = new HashSet<Bulb>(); string? first = null;
        async Task Each(IEnumerable<Bulb> l, Cmd c, bool count)
        {
            foreach (var b in l)
                try { await r.SendAsync(h, b, c, CancellationToken.None); if (count) reached.Add(b); }
                catch (Exception ex) { first ??= $"{b.Name}: {ex.Message}"; Diag.Log("fade", ex); }
        }
        if (fadeIn) { await Each(dim, new Cmd.Brightness(1), true); await Each(bulbs, new Cmd.Power(true), true); }
        var t0 = now();
        for (int k = 0; k < steps.Count; k++)
        {
            if (k > 0)
            {
                var wait = t0 + steps[k].AtMs - now();
                if (wait > 0) try { await delay(wait, ct); } catch (OperationCanceledException) { await Each(dim, new Cmd.Brightness(steps[^1].Pct), false); break; }
            }
            await Each(dim, new Cmd.Brightness(steps[k].Pct), k == 0);
            if (k == 0 && reached.Count == 0) return new BatchResult(0, bulbs.Count, first ?? "no dimmable bulb");
        }
        if (!fadeIn) await Each(bulbs, new Cmd.Power(false), dim.Count == 0);
        return new BatchResult(reached.Count, bulbs.Count, first);
    }
}
