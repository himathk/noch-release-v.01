using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Noch;

/// Something went wrong that the person can fix, said in words they can act on.
sealed class AskProblem(string message) : Exception(message);

/// Hands a spoken question to the coding agent the person already has, so their own plan pays for it.
abstract class Assistant
{
    public abstract string Name { get; }
    protected abstract string Command { get; }
    public string? Exe => _exe ??= Find(Command);
    string? _exe;

    /// Kept to plain sentences, because they are shown in a small card and may be read aloud later.
    public const string Brief = "You answer questions spoken aloud to a small notch at the top of the user's Windows screen. "
        + "The question was written down by speech recognition, so read past small mistakes in it. "
        + "Reply in plain text with no markdown, in at most three short sentences unless more is truly needed. "
        + "Search the web when the question needs current or local information. Never change files or run programs.";

    /// For listening in: the questions come from other people, in the middle of a conversation.
    public const string Meeting = "You quietly help the user during a live meeting or conversation, shown in a small notch at the top of their Windows screen. "
        + "You get a transcript written by speech recognition, so expect mistakes in it. Lines marked [Them] are the sound from the computer, usually other people on a call; "
        + "lines marked [You] are the user's microphone, the user or people in the room with them. "
        + "Reply in plain text with no markdown. Search the web when the question needs current information. Never change files or run programs.";

    /// The folder questions are asked from. It holds nothing, so no project's rules or tools come along.
    public static string Folder => Path.Combine(Settings.Folder, "ask");

    public static Assistant? Pick(Settings s)
    {
        Assistant claude = new ClaudeCode(), codex = new Codex();
        if (s.AnswerWithCodex && codex.Exe != null) return codex;
        return claude.Exe != null ? claude : codex.Exe != null ? codex : null;
    }

    public static bool Both => new ClaudeCode().Exe != null && new Codex().Exe != null;

    /// Asks and waits for the whole answer. `partial` hears the answer so far, while it is being written.
    public abstract Task<string> Ask(string question, Action<string>? partial, CancellationToken stop, string brief = Brief);
    /// Gets ready while the person is still talking, so less of the wait comes after.
    public virtual void Warm(string brief = Brief) { }
    /// Nothing is going to be asked after all.
    public virtual void Cool() { }

    /// Looks where the installers put it, the way a terminal would.
    static string? Find(string name)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Append(Path.Combine(home, ".local", "bin"))
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"));
        foreach (var d in dirs)
            foreach (var ext in new[] { ".exe", ".cmd" })
            {
                try { var f = Path.Combine(d.Trim('"'), name + ext); if (File.Exists(f)) return f; } catch (ArgumentException) { }
            }
        return null;
    }

    protected async Task<(int Code, string Out, string Err)> Run(IEnumerable<string> args, string input, CancellationToken stop)
    {
        Directory.CreateDirectory(Folder);
        var p = new ProcessStartInfo(Exe!)
        {
            WorkingDirectory = Folder, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var a in args) p.ArgumentList.Add(a);
        using var proc = Process.Start(p) ?? throw new AskProblem($"{Name} didn't start");
        await using (var w = proc.StandardInput) { w.AutoFlush = true; await w.WriteAsync(input); }
        var outTask = proc.StandardOutput.ReadToEndAsync(stop);
        var errTask = proc.StandardError.ReadToEndAsync(stop);
        try { await proc.WaitForExitAsync(stop); }
        catch (OperationCanceledException) { try { proc.Kill(true); } catch { } throw; }
        return (proc.ExitCode, await outTask, await errTask);
    }

    protected static bool SignedOut(string s) =>
        s.Contains("authenticate", StringComparison.OrdinalIgnoreCase) || s.Contains("not logged in", StringComparison.OrdinalIgnoreCase)
        || s.Contains("/login", StringComparison.OrdinalIgnoreCase) || s.Contains("sign in", StringComparison.OrdinalIgnoreCase);

    protected static string FirstLine(string s) => s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
}

sealed class ClaudeCode : Assistant
{
    public override string Name => "Claude";
    protected override string Command => "claude";
    /// Spoken questions are short and someone is waiting, so the quick model answers.
    const string Model = "sonnet";
    Process? _warm;
    string? _warmFor;

    Process Start(string text)
    {
        Directory.CreateDirectory(Folder);
        var brief = Path.Combine(Folder, text == Meeting ? "meeting.txt" : "brief.txt");
        File.WriteAllText(brief, text);
        var p = new ProcessStartInfo(Exe!)
        {
            WorkingDirectory = Folder, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        // Takes the question as a message whenever it comes, and streams the answer back.
        // Only web search and fetch; no hooks, plugins or MCP servers from the user's own setup, and nothing saved.
        foreach (var a in new[] { "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--include-partial-messages",
            "--model", Model, "--setting-sources", "project", "--strict-mcp-config", "--tools", "WebSearch,WebFetch", "--allowedTools", "WebSearch,WebFetch",
            "--no-session-persistence", "--system-prompt-file", brief })
            p.ArgumentList.Add(a);
        return Process.Start(p) ?? throw new AskProblem("Claude Code didn't start");
    }

    public override void Warm(string brief = Brief)
    {
        Cool();
        try { _warm = Start(brief); _warmFor = brief; } catch (Exception e) { Noch.Program.Log(e); }
    }

    public override void Cool() { var p = _warm; _warm = null; if (p != null) Kill(p); }

    static void Kill(Process p) { try { if (!p.HasExited) p.Kill(true); } catch { } p.Dispose(); }

    static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public override async Task<string> Ask(string question, Action<string>? partial, CancellationToken stop, string brief = Brief)
    {
        var proc = _warm is { HasExited: false } w && _warmFor == brief ? w : null;
        if (proc is null) Cool(); else _warm = null;
        proc ??= Start(brief);
        try
        {
            var err = proc.StandardError.ReadToEndAsync(CancellationToken.None);
            await proc.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { type = "user", message = new { role = "user", content = question } }));
            await proc.StandardInput.FlushAsync(stop);
            var text = new StringBuilder();
            string? result = null; var failed = false;
            while (await proc.StandardOutput.ReadLineAsync(stop) is { } line)
            {
                JsonElement root;
                try { using var d = JsonDocument.Parse(line); root = d.RootElement.Clone(); } catch (JsonException) { continue; }
                var type = Str(root, "type");
                if (type == "stream_event" && root.TryGetProperty("event", out var ev))
                {
                    // A new message after a search starts the answer over.
                    if (Str(ev, "type") == "message_start") text.Clear();
                    else if (Str(ev, "type") == "content_block_delta" && ev.TryGetProperty("delta", out var delta) && Str(delta, "type") == "text_delta")
                    {
                        text.Append(Str(delta, "text"));
                        partial?.Invoke(text.ToString());
                    }
                }
                else if (type == "result")
                {
                    result = Str(root, "result");
                    failed = root.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True;
                    break;
                }
            }
            if (result is null)
            {
                // It stopped before answering; whatever it said on the way out is the reason.
                proc.StandardInput.Close();
                await Task.WhenAny(err, Task.Delay(2000, CancellationToken.None));
                result = err.IsCompleted ? FirstLine(err.Result) : "";
                failed = true;
            }
            if (SignedOut(result)) throw new AskProblem("Claude Code is signed out. Run claude in a terminal to sign in");
            if (failed || string.IsNullOrWhiteSpace(result)) throw new AskProblem(result.Length > 0 ? "Claude: " + result : "Claude didn't answer");
            return result.Trim();
        }
        finally { Kill(proc); }
    }
}

sealed class Codex : Assistant
{
    public override string Name => "Codex";
    protected override string Command => "codex";

    public override async Task<string> Ask(string question, Action<string>? partial, CancellationToken stop, string brief = Brief)
    {
        Directory.CreateDirectory(Folder);
        var answer = Path.Combine(Folder, "answer.txt");
        File.Delete(answer);
        var (code, output, err) = await Run(["exec", "--skip-git-repo-check", "--sandbox", "read-only", "-c", "web_search=live", "-o", answer, "-"],
            brief + "\n\n" + question, stop);
        var result = File.Exists(answer) ? (await File.ReadAllTextAsync(answer, stop)).Trim() : "";
        if (result.Length > 0) return result;
        var said = FirstLine(err.Length > 0 ? err : output);
        if (SignedOut(err + output)) throw new AskProblem("Codex is signed out. Run codex in a terminal to sign in");
        throw new AskProblem(said.Length > 0 ? "Codex: " + said : $"Codex didn't answer ({code})");
    }
}
