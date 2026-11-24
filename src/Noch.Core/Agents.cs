using System.Text.Json.Nodes;

namespace Noch.Core;

public enum Activity { Idle, Thinking, Reading, Searching, Editing, Running, NeedsApproval, Question, Attention, Done, Failed }

/// One hook call as it arrived from an agent.
public sealed record HookEvent(string Source, long Ticks, IReadOnlyList<int> Pids, JsonObject Payload, int AgentPid = 0)
{
    public string Name => Str("hook_event_name") ?? "";
    public string? Str(string key) => Payload[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    public JsonObject Input => Payload["tool_input"] as JsonObject ?? new JsonObject();
    public string SessionId => Str("session_id") ?? Str("conversation_id") ?? "";
    public string Cwd => Str("cwd") is { Length: > 0 } c ? c
        : (Payload["workspace_roots"] as JsonArray)?.FirstOrDefault()?.GetValue<string>() ?? "";
}

/// Something an agent is blocked on until the user answers.
public sealed class PendingRequest
{
    public required string SessionKey { get; init; }
    public required HookEvent Event { get; init; }
    public bool IsQuestion { get; init; }
    public DateTime AskedAt { get; init; } = DateTime.UtcNow;
    readonly TaskCompletionSource<string> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<string> Answer => _answer.Task;
    internal bool Resolve(string json) => _answer.TrySetResult(json);

    public string Tool => Event.Str("tool_name") ?? "";
    /// The exact thing being approved. Never shortened: the user must be able to see all of it.
    public string Command => Describe.ApprovalBody(Tool, Event.Input);
    public string Title => Describe.ApprovalTitle(Tool);
    public JsonArray? Suggestions => Event.Payload["permission_suggestions"] is JsonArray { Count: > 0 } a ? a : null;
    public IReadOnlyList<Question> Questions => IsQuestion ? Question.Parse(Event.Input) : [];
}

public sealed record Question(string Text, bool Multi, IReadOnlyList<(string Label, string Detail)> Options)
{
    public static IReadOnlyList<Question> Parse(JsonObject input)
    {
        var list = new List<Question>();
        foreach (var q in (input["questions"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            var opts = new List<(string, string)>();
            foreach (var o in (q["options"] as JsonArray)?.OfType<JsonObject>() ?? [])
                opts.Add(((string?)o["label"] ?? "", (string?)o["description"] ?? ""));
            list.Add(new Question((string?)q["question"] ?? "", q["multiSelect"]?.GetValue<bool>() ?? false, opts));
        }
        return list;
    }
}

public sealed class AgentSession
{
    public required string Key { get; init; }
    public required string Source { get; init; }
    public string Agent => Source switch { "cursor" => "Cursor", "codex" => "Codex", _ => "Claude Code" };
    public string Cwd { get; internal set; } = "";
    public string Project => Cwd.Length == 0 ? Agent : Path.GetFileName(Cwd.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : Cwd;
    public Activity Activity { get; internal set; } = Activity.Idle;
    public string Label { get; internal set; } = "";
    public HashSet<string> Subagents { get; } = [];
    public DateTime TurnStarted { get; internal set; }
    public DateTime LastSeen { get; internal set; }
    public IReadOnlyList<int> Pids { get; internal set; } = [];
    /// The agent itself, not the shells between it and our helper. 0 when unknown.
    public int AgentPid { get; internal set; }
    public string? TranscriptPath { get; internal set; }
    /// Size of the conversation in tokens, when known.
    public long? ContextTokens { get; set; }
    /// The last thing it said, in plain words.
    public string LastMessage { get; internal set; } = "";
    internal long LastTicks;
    public bool Working => Activity is Activity.Thinking or Activity.Reading or Activity.Searching or Activity.Editing or Activity.Running;
}

/// A short message that drops down by itself: an agent finished or stopped.
public sealed record Toast(string SessionKey, bool Failed, string Title, string Detail, DateTime At)
{
    public string Body { get; init; } = "";
    public TimeSpan Life => TimeSpan.FromSeconds(Body.Length > 0 ? 8 : 4);
}

public enum Signal { NeedsYou, Finished, Failed }
