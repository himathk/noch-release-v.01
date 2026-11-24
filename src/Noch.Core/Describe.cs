using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Noch.Core;

/// Turns raw tool calls into short plain sentences.
public static class Describe
{
    static string S(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
    static string FileName(string p) => p.Length == 0 ? "a file" : Path.GetFileName(p.TrimEnd('\\', '/'));
    public static string OneLine(string s, int max)
    {
        s = string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return s.Length <= max ? s : s[..(max - 1)].TrimEnd() + "…";
    }

    public static (Activity, string) Tool(string tool, JsonObject i)
    {
        switch (tool)
        {
            case "Read" or "NotebookRead": return (Activity.Reading, "Reading " + FileName(S(i, "file_path")));
            case "Edit" or "MultiEdit" or "Write": return (Activity.Editing, "Editing " + FileName(S(i, "file_path")));
            case "NotebookEdit": return (Activity.Editing, "Editing " + FileName(S(i, "notebook_path")));
            case "Glob" or "Grep" or "LS": return (Activity.Searching, "Searching the project");
            case "WebSearch": return (Activity.Searching, "Searching the web");
            case "WebFetch": return (Activity.Reading, "Reading " + (Uri.TryCreate(S(i, "url"), UriKind.Absolute, out var u) ? u.Host : "a page"));
            case "Bash" or "PowerShell" or "Shell":
                var d = S(i, "description");
                return (Activity.Running, d.Length > 0 ? OneLine(d, 60) : "Running " + OneLine(S(i, "command"), 48));
            case "Task" or "Agent": return (Activity.Running, "Running a subagent");
            case "Skill": return (Activity.Thinking, "Using a skill");
            case "TodoWrite" or "TaskCreate" or "TaskUpdate": return (Activity.Thinking, "Planning");
        }
        if (tool.StartsWith("mcp__", StringComparison.Ordinal))
        {
            var parts = tool.Split("__");
            return (Activity.Running, "Using " + (parts.Length > 1 ? parts[1].Replace('_', ' ') : "a tool"));
        }
        return (Activity.Running, tool.Length > 0 ? "Using " + tool : "Working");
    }

    public static string ApprovalTitle(string tool) => tool switch
    {
        "Bash" or "PowerShell" or "Shell" => "wants to run a command",
        "Edit" or "MultiEdit" or "NotebookEdit" => "wants to edit a file",
        "Write" => "wants to write a file",
        "Read" => "wants to read a file",
        "WebFetch" => "wants to open a page",
        "WebSearch" => "wants to search the web",
        _ when tool.StartsWith("mcp__", StringComparison.Ordinal) => "wants to use " + (tool.Split("__") is { Length: > 1 } p ? p[1].Replace('_', ' ') : "a tool"),
        _ => "wants to use " + tool
    };

    static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string ApprovalBody(string tool, JsonObject i) => tool switch
    {
        "Bash" or "PowerShell" or "Shell" => S(i, "command"),
        "Edit" => $"{S(i, "file_path")}\n\n{Prefix("- ", S(i, "old_string"))}\n{Prefix("+ ", S(i, "new_string"))}",
        "Write" => $"{S(i, "file_path")}\n\n{Prefix("+ ", S(i, "content"))}",
        "Read" => S(i, "file_path"),
        "WebFetch" => S(i, "url"),
        "WebSearch" => S(i, "query"),
        _ => (tool.StartsWith("mcp__", StringComparison.Ordinal) ? tool + "\n" : "") + i.ToJsonString(Pretty)
    };

    static string Prefix(string p, string s) => string.Join('\n', s.Replace("\r", "").Split('\n').Select(l => p + l));

    public static string StopReason(string? error) => error switch
    {
        "rate_limit" => "usage limit reached",
        "authentication_failed" or "oauth_org_not_allowed" or "cloud_credential_error" => "sign-in problem",
        "billing_error" or "account_on_hold" => "billing problem",
        "server_error" => "server error",
        "max_output_tokens" => "answer was too long",
        _ => "something went wrong"
    };
}
