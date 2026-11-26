namespace Noch.Core;

public enum NotchState { Hidden, Rest, Strip, Compact, Approval, Question, Done, Failed, Notice, Peek, Player, Focus, Shelf, Voice }

/// What the user has opened by hand.
public enum Panel { None, Peek, Player, Focus, Shelf }

/// What the bar is carrying.
[Flags] public enum Show { None = 0, Agent = 1, Music = 2, Timer = 4, Meeting = 8 }

public sealed record Scene(bool Fullscreen, bool Covered, Panel Panel, bool Music = false, bool Timer = false, bool Meeting = false, Notice? Notice = null, bool Quiet = false, bool Voice = false)
{
    public Show Bar(bool agents) => (agents ? Show.Agent : 0) | (Music ? Show.Music : 0) | (Timer ? Show.Timer : 0) | (Meeting ? Show.Meeting : 0);
}

/// Decides the one thing the notch shows right now.
public static class Presenter
{
    public static NotchState Choose(AgentHub hub, Scene s)
    {
        var pending = hub.Pending;
        // Asked for by holding the keys, so it comes before everything, even over a full-screen window.
        if (s.Voice) return NotchState.Voice;
        if (s.Fullscreen) return pending.Count > 0 ? NotchState.Strip : NotchState.Hidden;
        switch (s.Panel)
        {
            case Panel.Player: return NotchState.Player;
            case Panel.Peek: return NotchState.Peek;
            case Panel.Focus: return NotchState.Focus;
            case Panel.Shelf: return NotchState.Shelf;
        }
        if (pending.Count > 0) return pending[0].IsQuestion ? NotchState.Question : NotchState.Approval;
        if (s.Notice is { Kind: NoticeKind.Meeting or NoticeKind.Volume }) return NotchState.Notice;
        // A focus block only lets through what has gone wrong.
        if (hub.Toast is { } t && (t.Failed || !s.Quiet)) return t.Failed ? NotchState.Failed : NotchState.Done;
        if (s.Notice is { } n && (!s.Quiet || n.Kind is NoticeKind.Timer or NoticeKind.Warning)) return NotchState.Notice;
        if (s.Bar(hub.Sessions.Any(Active)) == Show.None) return NotchState.Rest;
        return s.Covered ? NotchState.Strip : NotchState.Compact;
    }

    public static bool NeedsYou(AgentSession s) => s.Activity is Activity.NeedsApproval or Activity.Question or Activity.Attention;
    public static bool Active(AgentSession s) => s.Working || NeedsYou(s);

    /// Most urgent first, then the longest running.
    public static IReadOnlyList<AgentSession> Ordered(IEnumerable<AgentSession> list) =>
        [.. list.OrderBy(s => NeedsYou(s) ? 0 : s.Activity == Activity.Failed ? 1 : s.Working ? 2 : s.Activity == Activity.Done ? 3 : 4).ThenBy(s => s.TurnStarted)];
}
