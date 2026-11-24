using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

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

    /// Makes characters that hide or reorder text show up, so a command cannot look like something it is not.
    public static string Visible(string s)
    {
        if (!s.Any(Sneaky)) return s;
        var sb = new System.Text.StringBuilder(s.Length + 32);
        foreach (var c in s) if (Sneaky(c)) sb.Append($"[U+{(int)c:X4}]"); else sb.Append(c);
        return sb.ToString();
    }

    static bool Sneaky(char c) => c is >= '\u202A' and <= '\u202E' or >= '\u2066' and <= '\u2069' or >= '\u200B' and <= '\u200F' or '\u061C' or '\uFEFF' or '\u2060' or '\u00AD' or '\u0085' or '\u2028' or '\u2029'
        || c < ' ' && c is not ('\n' or '\r' or '\t') || c == '\u007F';

    /// The first couple of sentences of an answer, without its formatting.
    public static string Summary(string? markdown, int max = 220)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "";
        var lines = new List<string>();
        var code = false;
        foreach (var raw in markdown.Replace("\r", "").Split('\n'))
        {
            var l = raw.Trim();
            if (l.StartsWith("```", StringComparison.Ordinal)) { code = !code; continue; }
            if (code || l.Length == 0 || l.StartsWith('|') || l.All(c => c is '-' or '*' or '_' or '=')) continue;
            var heading = l.StartsWith('#');
            l = Regex.Replace(l, @"^(#+|[-*+>]|\d+[.)])\s+", "");
            l = Regex.Replace(l, @"!?\[([^\]]*)\]\([^)]*\)", "$1");
            l = Regex.Replace(l, @"(\*\*|__|`|~~)", "");
            l = Regex.Replace(l, @"(?<!\w)[*_](\S(?:.*?\S)?)[*_](?!\w)", "$1");
            if (l.Length == 0) continue;
            if (heading || !".!?:;,".Contains(l[^1])) l += ".";
            lines.Add(l);
            if (lines.Sum(x => x.Length) > max * 2) break;
        }
        return OneLine(string.Join(' ', lines), max);
    }

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
