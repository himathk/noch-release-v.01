namespace Noch.Core;

public enum NotchState { Hidden, Rest, Strip, Music, Agent, Both, Approval, Question, Done, Failed, Peek, Player }

public sealed record Scene(bool Fullscreen, bool Covered, bool MusicVisible, bool PeekOpen, bool PlayerOpen);

/// Decides the one thing the notch shows right now.
public static class Presenter
{
    public static NotchState Choose(AgentHub hub, Scene s)
    {
        var pending = hub.Pending;
        if (s.Fullscreen) return pending.Count > 0 ? NotchState.Strip : NotchState.Hidden;
        if (s.PlayerOpen) return NotchState.Player;
        if (s.PeekOpen) return NotchState.Peek;
        if (pending.Count > 0) return pending[0].IsQuestion ? NotchState.Question : NotchState.Approval;
        if (hub.Toast is { } t) return t.Failed ? NotchState.Failed : NotchState.Done;
        var agents = hub.Sessions.Any(Active);
        if (!agents && !s.MusicVisible) return NotchState.Rest;
        if (s.Covered) return NotchState.Strip;
        return agents ? s.MusicVisible ? NotchState.Both : NotchState.Agent : NotchState.Music;
    }

    public static bool NeedsYou(AgentSession s) => s.Activity is Activity.NeedsApproval or Activity.Question or Activity.Attention;
    public static bool Active(AgentSession s) => s.Working || NeedsYou(s);

    /// Most urgent first, then the longest running.
    public static IReadOnlyList<AgentSession> Ordered(IEnumerable<AgentSession> list) =>
        [.. list.OrderBy(s => NeedsYou(s) ? 0 : s.Activity == Activity.Failed ? 1 : s.Working ? 2 : s.Activity == Activity.Done ? 3 : 4).ThenBy(s => s.TurnStarted)];
}
