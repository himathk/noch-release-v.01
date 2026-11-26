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
    protected const string Brief = "You answer questions spoken aloud to a small notch at the top of the user's Windows screen. "
        + "The question was written down by speech recognition, so read past small mistakes in it. "
        + "Reply in plain text with no markdown, in at most three short sentences unless more is truly needed. "
        + "Search the web when the question needs current or local information. Never change files or run programs.";

    /// The folder questions are asked from. It holds nothing, so no project's rules or tools come along.
    public static string Folder => Path.Combine(Settings.Folder, "ask");

    public static Assistant? Pick(Settings s)
    {
        Assistant claude = new ClaudeCode(), codex = new Codex();
        if (s.AnswerWithCodex && codex.Exe != null) return codex;
        return claude.Exe != null ? claude : codex.Exe != null ? codex : null;
    }

    public static bool Both => new ClaudeCode().Exe != null && new Codex().Exe != null;

    public abstract Task<string> Ask(string question, CancellationToken stop);

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

    public override async Task<string> Ask(string question, CancellationToken stop)
    {
        Directory.CreateDirectory(Folder);
        var brief = Path.Combine(Folder, "brief.txt");
        await File.WriteAllTextAsync(brief, Brief, stop);
        // Only web search and fetch; no hooks, plugins or MCP servers from the user's own setup, and nothing saved.
        var (_, output, err) = await Run(["-p", "--output-format", "json", "--setting-sources", "project", "--strict-mcp-config",
            "--tools", "WebSearch,WebFetch", "--allowedTools", "WebSearch,WebFetch", "--no-session-persistence", "--system-prompt-file", brief], question, stop);
        string? result = null; var failed = true;
        try
        {
            using var doc = JsonDocument.Parse(output);
            result = doc.RootElement.TryGetProperty("result", out var r) ? r.GetString() : null;
            failed = doc.RootElement.TryGetProperty("is_error", out var e) && e.GetBoolean();
        }
        catch (JsonException) { }
        var said = result ?? FirstLine(err + "\n" + output);
        if (SignedOut(said)) throw new AskProblem("Claude Code is signed out. Run claude in a terminal to sign in");
        if (failed || string.IsNullOrWhiteSpace(result)) throw new AskProblem(said.Length > 0 ? "Claude: " + said : "Claude didn't answer");
        return result.Trim();
    }
}

sealed class Codex : Assistant
{
    public override string Name => "Codex";
    protected override string Command => "codex";

    public override async Task<string> Ask(string question, CancellationToken stop)
    {
        Directory.CreateDirectory(Folder);
        var answer = Path.Combine(Folder, "answer.txt");
        File.Delete(answer);
        var (code, output, err) = await Run(["exec", "--skip-git-repo-check", "--sandbox", "read-only", "-c", "web_search=live", "-o", answer, "-"],
            Brief + "\n\nThe question:\n" + question, stop);
        var result = File.Exists(answer) ? (await File.ReadAllTextAsync(answer, stop)).Trim() : "";
        if (result.Length > 0) return result;
        var said = FirstLine(err.Length > 0 ? err : output);
        if (SignedOut(err + output)) throw new AskProblem("Codex is signed out. Run codex in a terminal to sign in");
        throw new AskProblem(said.Length > 0 ? "Codex: " + said : $"Codex didn't answer ({code})");
    }
}
