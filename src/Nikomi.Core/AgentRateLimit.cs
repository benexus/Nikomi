namespace Nikomi.Core;

/// <summary>
/// How often an agent may ask the running app. Every answer is worked out on the window's thread, and a
/// large one reads details of up to 200 processes, so an agent asking in a tight loop would make the
/// window stutter. A short burst is fine (an agent often asks a few things at once); after that, questions
/// are answered at a steady rate and the rest are told to wait. Times are
/// <see cref="Environment.TickCount64"/> milliseconds.
/// </summary>
public sealed class AgentRateLimit(int burst = AgentRateLimit.Burst, double perSecond = AgentRateLimit.PerSecond)
{
    public const int Burst = 10;
    public const double PerSecond = 4;

    public const string Refusal = "Too many questions at once. Wait a second and ask again.";

    private double _tokens = burst;
    private long? _lastMs;

    /// <summary>True when one more question may be answered now; it is then counted.</summary>
    public bool TryTake(long nowMs)
    {
        if (_lastMs is { } last && nowMs > last)
            _tokens = Math.Min(burst, _tokens + (nowMs - last) * perSecond / 1000);
        _lastMs = nowMs;
        if (_tokens < 1) return false;
        _tokens--;
        return true;
    }
}
