namespace Wisp.Core.Sync;

/// <summary>Growing retry delays with jitter: 5 s, 15 s, 1 min, 5 min, then 15 min.</summary>
public sealed class Backoff
{
    static readonly TimeSpan[] Steps =
    [
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
    ];

    int _attempt;

    public int Attempt => _attempt;

    public TimeSpan Next() => For(_attempt++);

    public void Reset() => _attempt = 0;

    public static TimeSpan For(int attempt)
    {
        var step = Steps[Math.Clamp(attempt, 0, Steps.Length - 1)];
        var jitter = 0.8 + Random.Shared.NextDouble() * 0.4; // ±20 %
        return TimeSpan.FromMilliseconds(step.TotalMilliseconds * jitter);
    }
}
