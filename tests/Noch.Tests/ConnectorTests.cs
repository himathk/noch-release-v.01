using System.Text.Json.Nodes;
using Noch.Core;
using Xunit;

namespace Noch.Tests;

public sealed class ConnectorTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("noch").FullName;
    public void Dispose() => Directory.Delete(_dir, true);
    string Settings => Path.Combine(_dir, "settings.json");

    [Fact]
    public void Keeps_other_settings_and_hooks_and_undoes_itself()
    {
        File.WriteAllText(Settings, """{"model":"opus","hooks":{"Stop":[{"hooks":[{"type":"command","command":"say hi"}]}]}}""");
        var before = JsonNode.Parse(File.ReadAllText(Settings))!.ToJsonString();
        var c = new ClaudeConnector(_dir, @"C:\Users\A B\noch-hook.exe");
        Assert.False(c.Connected);
        c.Connect();
        c.Connect();
        Assert.True(c.Connected);
        var root = JsonNode.Parse(File.ReadAllText(Settings))!;
        Assert.Equal("opus", (string?)root["model"]);
        Assert.Equal(2, root["hooks"]!["Stop"]!.AsArray().Count);
        Assert.Equal(2, root["hooks"]!["PreToolUse"]!.AsArray().Count);
        Assert.Null(root["hooks"]!["PermissionRequest"]![0]!["hooks"]![0]!["async"]);
        c.Disconnect();
        Assert.Equal(before, JsonNode.Parse(File.ReadAllText(Settings))!.ToJsonString());
    }

    [Fact]
    public void Refuses_to_touch_broken_json()
    {
        File.WriteAllText(Settings, "{ nope");
        var c = new ClaudeConnector(_dir, "noch-hook.exe");
        Assert.Throws<InvalidOperationException>(c.Connect);
        Assert.Equal("{ nope", File.ReadAllText(Settings));
    }

    [Fact]
    public void Cursor_only_uses_events_that_cannot_approve()
    {
        var c = new CursorConnector(_dir, @"C:\x\noch-hook.exe");
        c.Connect();
        var hooks = JsonNode.Parse(File.ReadAllText(c.ConfigPath))!["hooks"]!.AsObject();
        Assert.DoesNotContain(hooks, h => h.Key is "beforeShellExecution" or "beforeMCPExecution" or "beforeReadFile" or "preToolUse" or "subagentStart");
        c.Disconnect();
        Assert.False(c.Connected);
    }
}
