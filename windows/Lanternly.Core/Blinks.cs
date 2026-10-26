namespace Lanternly.Core;

/// <summary>
/// Which bulbs are blinking ("Find this bulb"). Lives at window level so it survives row rebuilds, and lets
/// the app send every pending stop before it shuts down. Not thread-safe: use from the UI thread.
/// </summary>
public sealed class Blinks<TKey> where TKey : notnull
{
    readonly Dictionary<TKey, CancellationTokenSource> _active = new();

    public bool IsActive(TKey k) => _active.ContainsKey(k);
    public int Count => _active.Count;

    /// <summary>Marks k as blinking; the token is cancelled when the blink is stopped or replaced.</summary>
    public CancellationToken Start(TKey k)
    {
        Stop(k);
        var cts = new CancellationTokenSource();
        _active[k] = cts;
        return cts.Token;
    }

    /// <summary>Ends the blink for k (manual stop or failed start). True if it was active.</summary>
    public bool Stop(TKey k)
    {
        if (!_active.Remove(k, out var cts)) return false;
        cts.Cancel(); cts.Dispose();
        return true;
    }

    /// <summary>Timer end: true (and clears the state) only if the blink identified by token is still current.</summary>
    public bool Expire(TKey k, CancellationToken token)
    {
        if (!_active.TryGetValue(k, out var cts) || token.IsCancellationRequested || cts.Token != token) return false;
        _active.Remove(k); cts.Dispose();
        return true;
    }

    /// <summary>Stops all blinks and returns the bulbs that still need a stop command.</summary>
    public List<TKey> Drain()
    {
        var keys = _active.Keys.ToList();
        foreach (var k in keys) Stop(k);
        return keys;
    }
}
