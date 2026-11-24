namespace Noch.Core;

/// Keeps track of every agent session and everything waiting on the user.
/// All members are safe to call from any thread.
public sealed class AgentHub
{
    readonly object _gate = new();
    readonly Dictionary<string, AgentSession> _sessions = [];
    readonly List<PendingRequest> _pending = [];
    Toast? _toast;

    public event Action? Changed;
    /// When true (a game or film is fullscreen) questions go straight back to the terminal.
    public Func<bool> Busy { get; set; } = () => false;
    public TimeSpan QuestionTimeout { get; set; } = TimeSpan.FromMinutes(2);
    public TimeSpan MinTurnForToast { get; set; } = TimeSpan.FromSeconds(5);

    public IReadOnlyList<AgentSession> Sessions { get { lock (_gate) return [.. _sessions.Values]; } }
    public IReadOnlyList<PendingRequest> Pending { get { lock (_gate) return [.. _pending]; } }
    public Toast? Toast { get { lock (_gate) return _toast; } }
    public AgentSession? Find(string key) { lock (_gate) return _sessions.GetValueOrDefault(key); }

    /// Returns the text to print back to the agent. Empty means "no opinion".
    public async Task<string> HandleAsync(HookEvent e, CancellationToken gone = default)
    {
        PendingRequest? wait;
        lock (_gate) wait = Apply(e);
        Changed?.Invoke();
        if (wait is null) return e is { Source: "cursor", Name: "beforeSubmitPrompt" } ? "{\"continue\":true}" : "";

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(gone);
        if (wait.IsQuestion) limit.CancelAfter(QuestionTimeout);
        // The agent hung up (answered in the terminal, or was closed), or time ran out.
        using var _ = limit.Token.Register(() => Finish(wait, ""));
        return await wait.Answer.ConfigureAwait(false);
    }

    PendingRequest? Apply(HookEvent e)
    {
        if (e.SessionId.Length == 0) return null;
        var key = e.Source + ":" + e.SessionId;
        if (e.Name is "SessionEnd" or "sessionEnd") { Drop(key); return null; }

        if (!_sessions.TryGetValue(key, out var s))
            _sessions[key] = s = new AgentSession { Key = key, Source = e.Source, TurnStarted = DateTime.UtcNow };
        if (e.Cwd.Length > 0 && s.Cwd.Length == 0) s.Cwd = e.Cwd;
        if (e.Pids.Count > 0) s.Pids = e.Pids;
        if (e.AgentPid > 0) s.AgentPid = e.AgentPid;
        s.LastSeen = DateTime.UtcNow;

        // Background hooks can overtake each other. Ignore anything older than what we already know.
        var asks = e.Name == "PermissionRequest" || IsQuestionCall(e);
        if (!asks && e.Ticks < s.LastTicks) return null;
        s.LastTicks = Math.Max(s.LastTicks, e.Ticks);

        return e.Source == "cursor" ? ApplyCursor(s, e) : ApplyClaude(s, e);
    }

    static bool IsQuestionCall(HookEvent e) => e.Name == "PreToolUse" && e.Str("tool_name") == "AskUserQuestion";

    PendingRequest? ApplyClaude(AgentSession s, HookEvent e)
    {
        var tool = e.Str("tool_name") ?? "";
        // A subagent carrying on does not mean the main agent's request was answered.
        var sub = e.Str("agent_id") is { Length: > 0 };
        switch (e.Name)
        {
            case "SessionStart": if (!HasPending(s.Key)) Set(s, Activity.Idle, ""); break;
            case "UserPromptSubmit":
                Release(s.Key); s.Subagents.Clear(); s.TurnStarted = DateTime.UtcNow;
                if (_toast?.SessionKey == s.Key) _toast = null;
                Set(s, Activity.Thinking, "Thinking"); break;

            case "PreToolUse" when tool == "AskUserQuestion":
                if (Busy()) { Set(s, Activity.Attention, "Has a question for you"); return null; }
                return Ask(s, e, question: true);
            case "PreToolUse":
                Begin(s);
                if (!HasPending(s.Key)) { var (a, l) = Describe.Tool(tool, e.Input); Set(s, a, l); }
                break;

            case "PermissionRequest" when tool == "AskUserQuestion": Set(s, Activity.Attention, "Has a question for you"); break;
            case "PermissionRequest" when tool == "ExitPlanMode": Set(s, Activity.Attention, "Has a plan for you to review"); break;
            case "PermissionRequest": return Ask(s, e, question: false);

            case "PostToolUse" or "PostToolUseFailure":
                if (!sub) ReleaseMatching(s.Key, e);
                Begin(s);
                if (!HasPending(s.Key)) Set(s, Activity.Thinking, "Thinking");
                break;

            case "SubagentStart": if (e.Str("agent_id") is { } id) s.Subagents.Add(id); break;
            case "SubagentStop": if (e.Str("agent_id") is { } sid) s.Subagents.Remove(sid); break;

            case "Notification":
                if (e.Str("notification_type") == "idle_prompt" && !HasPending(s.Key)) Set(s, Activity.Idle, "");
                break;

            case "Stop": Release(s.Key); Finished(s, false, null); break;
            case "StopFailure": Release(s.Key); Finished(s, true, Describe.StopReason(e.Str("error"))); break;
        }
        return null;
    }

    PendingRequest? ApplyCursor(AgentSession s, HookEvent e)
    {
        switch (e.Name)
        {
            case "beforeSubmitPrompt":
                s.TurnStarted = DateTime.UtcNow;
                if (_toast?.SessionKey == s.Key) _toast = null;
                Set(s, Activity.Thinking, "Thinking"); break;
            case "afterAgentThought": Begin(s); Set(s, Activity.Thinking, "Thinking"); break;
            case "afterFileEdit": Begin(s); Set(s, Activity.Editing, "Editing " + Path.GetFileName(e.Str("file_path") ?? "a file")); break;
            case "afterShellExecution": Begin(s); Set(s, Activity.Running, "Ran " + Describe.OneLine(e.Str("command") ?? "a command", 48)); break;
            case "postToolUse": Begin(s); var (a, l) = Describe.Tool(e.Str("tool_name") ?? "", e.Input); Set(s, a, l); break;
            case "stop":
                var status = e.Str("status");
                if (status == "aborted") Set(s, Activity.Idle, "");
                else Finished(s, status == "error", status == "error" ? "something went wrong" : null);
                break;
        }
        return null;
    }

    static void Set(AgentSession s, Activity a, string label) { s.Activity = a; s.Label = label; }
    static void Begin(AgentSession s) { if (s.Activity is Activity.Idle or Activity.Done or Activity.Failed) s.TurnStarted = DateTime.UtcNow; }
    bool HasPending(string key) => _pending.Any(p => p.SessionKey == key);

    PendingRequest Ask(AgentSession s, HookEvent e, bool question)
    {
        var p = new PendingRequest { SessionKey = s.Key, Event = e, IsQuestion = question };
        _pending.Add(p);
        Set(s, question ? Activity.Question : Activity.NeedsApproval, question ? "Has a question for you" : char.ToUpperInvariant(p.Title[0]) + p.Title[1..]);
        return p;
    }

    void Finished(AgentSession s, bool failed, string? why)
    {
        var length = DateTime.UtcNow - s.TurnStarted;
        Set(s, failed ? Activity.Failed : Activity.Done, failed ? "Stopped, " + why : "Finished");
        if (failed || length >= MinTurnForToast)
            _toast = new Toast(s.Key, failed, s.Agent + (failed ? " stopped" : " finished"),
                failed ? $"{s.Project}, {why}" : $"{s.Project}, took {Clock(length)}", DateTime.UtcNow);
    }

    public static string Clock(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";

    // ---- answers ----

    public void Allow(PendingRequest p) => Finish(p, Answers.Allow());
    public void AllowAlways(PendingRequest p) => Finish(p, Answers.AllowAlways(p.Suggestions!));
    public void Deny(PendingRequest p) => Finish(p, Answers.Deny());
    public void AnswerQuestions(PendingRequest p, IReadOnlyDictionary<string, string> answers) => Finish(p, Answers.Question(p.Event.Input, answers));
    /// Hands the decision back to the terminal.
    public void PassOn(PendingRequest p) => Finish(p, "", passedOn: true);
    public void DismissToast() { lock (_gate) _toast = null; Changed?.Invoke(); }

    void Finish(PendingRequest p, string json, bool passedOn = false)
    {
        lock (_gate)
        {
            if (!_pending.Remove(p)) return;
            if (_sessions.TryGetValue(p.SessionKey, out var s) && !HasPending(s.Key))
            {
                if (passedOn) Set(s, Activity.Attention, "Waiting for you in the terminal");
                else Set(s, Activity.Thinking, "Thinking");
            }
        }
        p.Resolve(json);
        Changed?.Invoke();
    }

    void Release(string key)
    {
        foreach (var p in _pending.Where(p => p.SessionKey == key).ToList()) { _pending.Remove(p); p.Resolve(""); }
    }

    void ReleaseMatching(string key, HookEvent e)
    {
        var tool = e.Str("tool_name");
        var input = e.Input.ToJsonString();
        foreach (var p in _pending.Where(p => p.SessionKey == key && p.Tool == tool && p.Event.Input.ToJsonString() == input).ToList()) { _pending.Remove(p); p.Resolve(""); }
    }

    void Drop(string key)
    {
        Release(key);
        _sessions.Remove(key);
        if (_toast is { Failed: false } t && t.SessionKey == key) _toast = null;
    }

    /// Clears out sessions whose process is gone or that went quiet (pressing Esc fires no hook).
    public void Sweep(Func<int, bool> alive, DateTime now)
    {
        var changed = false;
        lock (_gate)
        {
            foreach (var s in _sessions.Values.ToList())
            {
                var quiet = now - s.LastSeen;
                // A waiting request has its own open connection, which tells us when the agent goes away.
                if (HasPending(s.Key)) continue;
                if (s.AgentPid > 0 && !alive(s.AgentPid) || quiet > TimeSpan.FromHours(12)) { Drop(s.Key); changed = true; continue; }
                var limit = s.Activity == Activity.Thinking ? TimeSpan.FromMinutes(5) : TimeSpan.FromMinutes(15);
                if (s.Working && quiet > limit) { Set(s, Activity.Idle, ""); changed = true; }
            }
            if (_toast is { Failed: false } t && now - t.At > TimeSpan.FromSeconds(4)) { _toast = null; changed = true; }
        }
        if (changed) Changed?.Invoke();
    }
}
