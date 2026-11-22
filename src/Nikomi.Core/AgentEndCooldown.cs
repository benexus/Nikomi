namespace Nikomi.Core;

/// <summary>
/// After an agent's end request that the user did not confirm, further requests are refused for a minute,
/// whatever became of it: declined, not answered, not shown or withdrawn. Every request brings Nikomi's
/// window forward and shows a notification, so an agent that asks again in a loop must not be able to
/// do that over and over. Times are <see cref="Environment.TickCount64"/> milliseconds.
/// </summary>
public sealed class AgentEndCooldown
{
    public static readonly TimeSpan Length = TimeSpan.FromMinutes(1);

    private long? _since;
    private EndAnswer _after;

    public void Record(EndAnswer answer, long nowMs)
    {
        if (answer == EndAnswer.Confirmed) _since = null;
        else (_since, _after) = (nowMs, answer);
    }

    /// <summary>Why a new request is refused now, or null when it may be shown.</summary>
    public string? Refusal(long nowMs)
    {
        if (_since is not { } since || nowMs - since >= Length.TotalMilliseconds) return null;
        return _after switch
        {
            EndAnswer.Declined => "The user declined a request less than a minute ago. Do not ask again unless the user tells you to.",
            EndAnswer.Withdrawn => "The user turned agent access off while the last request was waiting. Do not ask again unless the user tells you to.",
            EndAnswer.Expired => "The user did not answer the last request. Wait at least a minute, and ask again only if it is still needed.",
            _ => "Nikomi could not show the last request. Wait at least a minute before asking again.",
        };
    }
}
