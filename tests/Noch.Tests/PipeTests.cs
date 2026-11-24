using System.Diagnostics;
using System.Text.Json.Nodes;
using Noch.Core;
using Xunit;

namespace Noch.Tests;

/// Runs the real noch-hook.exe against a real server.
public class PipeTests
{
    static string Exe()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (!Directory.Exists(Path.Combine(d!.FullName, "src"))) d = d.Parent;
        return Directory.GetFiles(Path.Combine(d.FullName, "src", "Noch.Hook", "bin"), "noch-hook.exe", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).First();
    }

    static async Task<(string Out, int Code)> Call(string json, bool kill = false)
    {
        using var p = Process.Start(new ProcessStartInfo(Exe(), "claude") { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false, StandardInputEncoding = new System.Text.UTF8Encoding(false) })!;
        await p.StandardInput.WriteAsync(json);
        p.StandardInput.Close();
        if (kill) { await Task.Delay(700); p.Kill(); }
        var o = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (o, p.ExitCode);
    }

    [Fact]
    public async Task Full_round_trip_then_closed_notch()
    {
        if (Directory.GetFiles(@"\\.\pipe\").Any(f => f.EndsWith(HookServer.DefaultPipe))) return; // a real notch is running
        var hub = new AgentHub();
        using (var server = new HookServer(hub))
        {
            server.Start();
            var first = await Call("""{"session_id":"a","cwd":"C:\\x\\é","hook_event_name":"UserPromptSubmit","prompt":"hi"}""");
            Assert.Equal(("", 0), first);
            Assert.Equal("é", hub.Sessions[0].Project);
            Assert.Contains(Environment.ProcessId, hub.Sessions[0].Pids);

            var ask = Call("""{"session_id":"a","hook_event_name":"PermissionRequest","tool_name":"Bash","tool_input":{"command":"dir"}}""");
            while (hub.Pending.Count == 0) await Task.Delay(20);
            hub.Allow(hub.Pending[0]);
            Assert.Equal("allow", (string?)JsonNode.Parse((await ask).Out)!["hookSpecificOutput"]!["decision"]!["behavior"]);

            var killed = Call("""{"session_id":"a","hook_event_name":"PermissionRequest","tool_name":"Bash","tool_input":{"command":"dir"}}""", kill: true);
            while (hub.Pending.Count == 0) await Task.Delay(20);
            await killed;
            for (var i = 0; i < 50 && hub.Pending.Count > 0; i++) await Task.Delay(40);
            Assert.Empty(hub.Pending);

            Assert.Equal("", (await Call("not json")).Out);
        }
        await Task.Delay(300);
        var sw = Stopwatch.StartNew();
        Assert.Equal(("", 0), await Call("""{"session_id":"a","hook_event_name":"Stop"}"""));
        Assert.True(sw.ElapsedMilliseconds < 1500);
    }
}
