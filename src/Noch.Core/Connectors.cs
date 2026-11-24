using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Noch.Core;

/// Adds and removes our hooks in an agent's own config file, leaving everything else as it was.
public abstract class Connector(string path, string hookExe)
{
    protected const string Mark = "noch-hook";
    protected string HookExe => hookExe;
    public string ConfigPath => path;
    public bool AgentInstalled => Directory.Exists(Path.GetDirectoryName(path));
    static readonly JsonSerializerOptions Opt = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    protected abstract void AddHooks(JsonObject root);
    protected abstract bool IsOurs(JsonNode? entry);
    /// Anything beyond hooks that was added.
    protected virtual bool StripExtras(JsonObject root) => false;

    public bool Connected
    {
        get
        {
            try { return File.Exists(path) && Load()["hooks"] is JsonObject h && h.Any(kv => kv.Value is JsonArray a && a.Any(IsOurs)); }
            catch { return false; }
        }
    }

    public void Connect()
    {
        var root = Load();
        Strip(root);
        AddHooks(root);
        Save(root);
    }

    public void Disconnect()
    {
        if (!File.Exists(path)) return;
        var root = Load();
        if (Strip(root) | StripExtras(root)) Save(root);
    }

    protected void Change(Action<JsonObject> a)
    {
        var root = Load();
        a(root);
        Save(root);
    }

    protected JsonObject? Peek() { try { return File.Exists(path) ? Load() : null; } catch { return null; } }

    JsonObject Load()
    {
        if (!File.Exists(path)) return [];
        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text)) return [];
        try { return JsonNode.Parse(text) as JsonObject ?? throw new JsonException(); }
        catch (JsonException) { throw new InvalidOperationException($"{path} is not valid JSON, so it was left alone."); }
    }

    bool Strip(JsonObject root)
    {
        if (root["hooks"] is not JsonObject hooks) return false;
        var changed = false;
        foreach (var (name, value) in hooks.ToList())
        {
            if (value is not JsonArray list) continue;
            var ours = list.Where(IsOurs).ToList();
            foreach (var entry in ours) list.Remove(entry);
            if (ours.Count > 0 && list.Count == 0) hooks.Remove(name);
            changed |= ours.Count > 0;
        }
        if (changed && hooks.Count == 0) root.Remove("hooks");
        return changed;
    }

    void Save(JsonObject root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var backup = path + ".before-noch";
        if (File.Exists(path) && !File.Exists(backup)) File.Copy(path, backup);
        var tmp = path + ".noch-tmp";
        File.WriteAllText(tmp, root.ToJsonString(Opt) + "\n");
        File.Move(tmp, path, overwrite: true);
    }

    protected static JsonArray List(JsonObject root, string ev)
    {
        if (root["hooks"] is not JsonObject hooks) root["hooks"] = hooks = [];
        if (hooks[ev] is not JsonArray list) hooks[ev] = list = [];
        return list;
    }
}

public sealed class ClaudeConnector(string configDir, string hookExe) : Connector(Path.Combine(configDir, "settings.json"), hookExe)
{
    public static string DefaultDir => Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } d ? d
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

    static readonly string[] Background = ["SessionStart", "UserPromptSubmit", "PostToolUse", "PostToolUseFailure", "Notification", "SubagentStart", "SubagentStop"];
    static readonly string[] Quick = ["Stop", "StopFailure", "SessionEnd"];

    protected override bool IsOurs(JsonNode? entry) => entry is JsonObject o && o["hooks"] is JsonArray a
        && a.Any(h => h is JsonObject x && ((string?)x["command"] ?? "").Contains(Mark, StringComparison.OrdinalIgnoreCase));

    protected override bool StripExtras(JsonObject root)
    {
        if (!OurStatusLine(root)) return false;
        root.Remove("statusLine");
        return true;
    }

    static bool OurStatusLine(JsonObject? root) => ((string?)(root?["statusLine"] as JsonObject)?["command"] ?? "").Contains(Mark, StringComparison.OrdinalIgnoreCase);

    /// Claude Code in a terminal tells its status line how much of the plan is used. Nothing else is told.
    public bool StatusLineOn => OurStatusLine(Peek());
    public bool HasOtherStatusLine => Peek() is { } r && r["statusLine"] != null && !OurStatusLine(r);

    public void SetStatusLine(bool on) => Change(root =>
    {
        if (root["statusLine"] != null && !OurStatusLine(root)) throw new InvalidOperationException("You already have your own status line, so it was left alone.");
        root.Remove("statusLine");
        if (!on) return;
        var exe = HookExe.Replace('\\', '/');
        root["statusLine"] = new JsonObject { ["type"] = "command", ["command"] = (exe.Contains(' ') ? $"\"{exe}\"" : exe) + " status" };
    });

    JsonObject Entry(int timeout, string? matcher = null, bool background = false, string? status = null)
    {
        // Exec form: no shell in between, so spaces in the path need no quoting.
        var h = new JsonObject { ["type"] = "command", ["command"] = HookExe.Replace('\\', '/'), ["args"] = new JsonArray("claude"), ["timeout"] = timeout };
        if (background) h["async"] = true;
        if (status != null) h["statusMessage"] = status;
        var e = new JsonObject();
        if (matcher != null) e["matcher"] = matcher;
        e["hooks"] = new JsonArray(h);
        return e;
    }

    protected override void AddHooks(JsonObject root)
    {
        foreach (var ev in Background) List(root, ev).Add(Entry(10, background: true));
        foreach (var ev in Quick) List(root, ev).Add(Entry(5));
        List(root, "PermissionRequest").Add(Entry(600));
        // The catch-all skips questions, or they would be reported twice.
        List(root, "PreToolUse").Add(Entry(10, "^(?!AskUserQuestion$).*", background: true));
        List(root, "PreToolUse").Add(Entry(150, "AskUserQuestion", status: "Waiting for your answer in the notch"));
    }
}

public sealed class CursorConnector(string cursorDir, string hookExe) : Connector(Path.Combine(cursorDir, "hooks.json"), hookExe)
{
    public static string DefaultDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cursor");

    // Only events that cannot grant anything: the notch watches Cursor, it never approves for it.
    static readonly string[] Events = ["beforeSubmitPrompt", "afterAgentThought", "afterFileEdit", "afterShellExecution", "postToolUse", "stop", "sessionEnd"];

    protected override bool IsOurs(JsonNode? entry) => entry is JsonObject o && ((string?)o["command"] ?? "").Contains(Mark, StringComparison.OrdinalIgnoreCase);

    protected override void AddHooks(JsonObject root)
    {
        root["version"] ??= 1;
        // Cursor runs hook commands through PowerShell on Windows.
        foreach (var ev in Events) List(root, ev).Add(new JsonObject { ["command"] = $"& '{HookExe.Replace("'", "''")}' cursor", ["timeout"] = 5 });
    }
}

/// Codex uses nearly the same hooks as Claude Code. It asks the user to trust them once, with /hooks.
public sealed class CodexConnector(string codexDir, string hookExe) : Connector(Path.Combine(codexDir, "hooks.json"), hookExe)
{
    public static string DefaultDir => Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } d ? d
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");

    static readonly string[] Background = ["SessionStart", "UserPromptSubmit", "PreToolUse", "PostToolUse", "SubagentStart", "SubagentStop", "Stop"];

    protected override bool IsOurs(JsonNode? entry) => entry is JsonObject o && o["hooks"] is JsonArray a
        && a.Any(h => h is JsonObject x && ((string?)x["command"] ?? "").Contains(Mark, StringComparison.OrdinalIgnoreCase));

    JsonObject Entry(int timeout, bool background)
    {
        var command = $"\"{HookExe}\" codex";
        var h = new JsonObject { ["type"] = "command", ["command"] = command, ["commandWindows"] = command, ["timeout"] = timeout };
        if (background) h["async"] = true;
        return new JsonObject { ["hooks"] = new JsonArray(h) };
    }

    protected override void AddHooks(JsonObject root)
    {
        foreach (var ev in Background) List(root, ev).Add(Entry(10, true));
        List(root, "PermissionRequest").Add(Entry(600, false));
    }
}
