namespace Noch.Core;

/// What is saved, so a timer survives a restart.
public sealed class FocusState
{
    public DateTime? EndsAt { get; set; }
    public double? PausedLeft { get; set; }
    public double Length { get; set; }
    public bool Break { get; set; }
}

public sealed class FocusTimer(FocusState state)
{
    public static readonly TimeSpan Longest = TimeSpan.FromHours(4);
    public event Action? Changed;
    /// True when a break just ended, false when a focus block did.
    public event Action<bool>? Finished;

    public FocusState State => state;
    public bool Running => state.EndsAt != null;
    public bool Paused => state.PausedLeft != null;
    public bool Active => Running || Paused;
    public bool IsBreak => Active && state.Break;
    /// Only a running focus block is quiet. A break or a pause is not.
    public bool Quiet => Running && !state.Break;

    public TimeSpan Left(DateTime now) => state.PausedLeft is { } s ? TimeSpan.FromSeconds(s) : state.EndsAt is { } e && e > now ? e - now : TimeSpan.Zero;
    public double Fraction(DateTime now) => state.Length <= 0 ? 0 : Math.Clamp(Left(now).TotalSeconds / state.Length, 0, 1);

    public void Start(TimeSpan length, bool isBreak, DateTime now)
    {
        length = length > Longest ? Longest : length;
        state.Length = length.TotalSeconds; state.Break = isBreak; state.PausedLeft = null; state.EndsAt = now + length;
        Changed?.Invoke();
    }

    public void Pause(DateTime now)
    {
        if (!Running) return;
        state.PausedLeft = Left(now).TotalSeconds; state.EndsAt = null;
        Changed?.Invoke();
    }

    public void Resume(DateTime now)
    {
        if (state.PausedLeft is not { } left) return;
        state.EndsAt = now + TimeSpan.FromSeconds(left); state.PausedLeft = null;
        Changed?.Invoke();
    }

    public void Add(TimeSpan more, DateTime now)
    {
        if (!Active) return;
        var left = Left(now) + more;
        if (left > Longest) left = Longest;
        if (left < TimeSpan.FromSeconds(1)) left = TimeSpan.FromSeconds(1);
        state.Length = Math.Max(state.Length, left.TotalSeconds);
        if (Paused) state.PausedLeft = left.TotalSeconds; else state.EndsAt = now + left;
        Changed?.Invoke();
    }

    public void Stop()
    {
        state.EndsAt = null; state.PausedLeft = null; state.Length = 0;
        Changed?.Invoke();
    }

    public void Tick(DateTime now)
    {
        if (state.EndsAt is not { } end || now < end) return;
        var wasBreak = state.Break;
        Stop();
        // If the PC was off or asleep when it ended, there is nothing left to announce.
        if (now - end < TimeSpan.FromMinutes(10)) Finished?.Invoke(wasBreak);
    }
}
