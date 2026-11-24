using System.Text.Json.Nodes;
using Noch.Core;
using Xunit;

namespace Noch.Tests;

public class HubTests
{
    long _t = 1;
    HookEvent E(string name, string extra = "", string source = "claude", string id = "s1", long? ticks = null) =>
        new(source, ticks ?? _t++, [1], (JsonObject)JsonNode.Parse($$"""{"session_id":"{{id}}","cwd":"D:\\Work\\ict","hook_event_name":"{{name}}"{{(extra.Length > 0 ? "," + extra : "")}}}""")!, 1);
    const string Bash = "\"tool_name\":\"Bash\",\"tool_input\":{\"command\":\"npm test\"}";

    [Fact]
    public async Task Follows_a_turn()
    {
        var hub = new AgentHub { MinTurnForToast = TimeSpan.Zero };
        await hub.HandleAsync(E("UserPromptSubmit"));
        Assert.Equal(Activity.Thinking, hub.Sessions[0].Activity);
        await hub.HandleAsync(E("PreToolUse", "\"tool_name\":\"Edit\",\"tool_input\":{\"file_path\":\"D:\\\\a\\\\auth.ts\"}"));
        Assert.Equal("Editing auth.ts", hub.Sessions[0].Label);
        Assert.Equal("ict", hub.Sessions[0].Project);
        await hub.HandleAsync(E("Stop"));
        Assert.Equal(Activity.Done, hub.Sessions[0].Activity);
        Assert.Equal("Claude Code finished", hub.Toast!.Title);
        hub.Sweep(_ => true, DateTime.UtcNow.AddSeconds(5));
        Assert.Null(hub.Toast);
    }

    [Fact]
    public async Task Late_background_event_does_not_undo_stop()
    {
        var hub = new AgentHub();
        await hub.HandleAsync(E("UserPromptSubmit", ticks: 10));
        await hub.HandleAsync(E("Stop", ticks: 30));
        await hub.HandleAsync(E("PostToolUse", Bash, ticks: 20));
        Assert.Equal(Activity.Done, hub.Sessions[0].Activity);
    }

    [Fact]
    public async Task Allow_and_deny_answer_the_agent()
    {
        var hub = new AgentHub();
        var a = hub.HandleAsync(E("PermissionRequest", Bash));
        Assert.Equal(NotchState.Approval, Presenter.Choose(hub, new Scene(false, true, true, false, false)));
        Assert.Equal("npm test", hub.Pending[0].Command);
        Assert.False(a.IsCompleted);
        hub.Allow(hub.Pending[0]);
        Assert.Equal("allow", (string?)JsonNode.Parse(await a)!["hookSpecificOutput"]!["decision"]!["behavior"]);
        var d = hub.HandleAsync(E("PermissionRequest", Bash));
        hub.Deny(hub.Pending[0]);
        Assert.Equal("deny", (string?)JsonNode.Parse(await d)!["hookSpecificOutput"]!["decision"]!["behavior"]);
    }

    [Fact]
    public async Task Always_echoes_the_suggested_rule()
    {
        var hub = new AgentHub();
        var a = hub.HandleAsync(E("PermissionRequest", Bash + ",\"permission_suggestions\":[{\"type\":\"addRules\",\"rules\":[{\"toolName\":\"Bash\",\"ruleContent\":\"npm test\"}],\"behavior\":\"allow\",\"destination\":\"localSettings\"}]"));
        hub.AllowAlways(hub.Pending[0]);
        Assert.Equal("addRules", (string?)JsonNode.Parse(await a)!["hookSpecificOutput"]!["decision"]!["updatedPermissions"]![0]!["type"]);
    }

    [Fact]
    public async Task Agent_hanging_up_closes_the_card()
    {
        var hub = new AgentHub();
        using var gone = new CancellationTokenSource();
        var a = hub.HandleAsync(E("PermissionRequest", Bash), gone.Token);
        gone.Cancel();
        Assert.Equal("", await a);
        Assert.Empty(hub.Pending);
    }

    [Fact]
    public async Task Subagent_work_does_not_close_the_main_agents_card()
    {
        var hub = new AgentHub();
        var a = hub.HandleAsync(E("PermissionRequest", Bash));
        await hub.HandleAsync(E("PostToolUse", Bash + ",\"agent_id\":\"x\""));
        await hub.HandleAsync(E("PreToolUse", "\"tool_name\":\"Read\",\"tool_input\":{\"file_path\":\"a\"},\"agent_id\":\"x\""));
        Assert.Single(hub.Pending);
        Assert.Equal(Activity.NeedsApproval, hub.Sessions[0].Activity);
        await hub.HandleAsync(E("PostToolUse", Bash));
        Assert.Empty(hub.Pending);
        Assert.Equal("", await a);
    }

    [Fact]
    public async Task Question_is_answered_with_the_original_input_plus_answers()
    {
        var hub = new AgentHub();
        var a = hub.HandleAsync(E("PreToolUse", "\"tool_name\":\"AskUserQuestion\",\"tool_input\":{\"questions\":[{\"question\":\"Which?\",\"header\":\"H\",\"multiSelect\":false,\"options\":[{\"label\":\"A\",\"description\":\"d\"},{\"label\":\"B\",\"description\":\"\"}]}]}"));
        var q = hub.Pending[0].Questions[0];
        Assert.Equal(("A", "d"), q.Options[0]);
        hub.AnswerQuestions(hub.Pending[0], new Dictionary<string, string> { ["Which?"] = "B" });
        var o = JsonNode.Parse(await a)!["hookSpecificOutput"]!;
        Assert.Equal("allow", (string?)o["permissionDecision"]);
        Assert.Equal("B", (string?)o["updatedInput"]!["answers"]!["Which?"]);
        Assert.Equal("Which?", (string?)o["updatedInput"]!["questions"]![0]!["question"]);
    }

    [Fact]
    public async Task Questions_time_out_and_go_back_to_the_terminal()
    {
        var hub = new AgentHub { QuestionTimeout = TimeSpan.FromMilliseconds(50) };
        Assert.Equal("", await hub.HandleAsync(E("PreToolUse", "\"tool_name\":\"AskUserQuestion\",\"tool_input\":{\"questions\":[]}")));
    }

    [Fact]
    public async Task Questions_skip_the_notch_when_fullscreen()
    {
        var hub = new AgentHub { Busy = () => true };
        Assert.Equal("", await hub.HandleAsync(E("PreToolUse", "\"tool_name\":\"AskUserQuestion\",\"tool_input\":{}")));
        Assert.Equal(Activity.Attention, hub.Sessions[0].Activity);
    }

    [Fact]
    public async Task Dead_process_and_quiet_sessions_are_cleared()
    {
        var hub = new AgentHub();
        await hub.HandleAsync(E("UserPromptSubmit"));
        hub.Sweep(_ => true, DateTime.UtcNow.AddMinutes(6));
        Assert.Equal(Activity.Idle, hub.Sessions[0].Activity);
        var ask = hub.HandleAsync(E("PermissionRequest", Bash));
        hub.Sweep(_ => false, DateTime.UtcNow);
        Assert.Single(hub.Pending);
        hub.Deny(hub.Pending[0]);
        await ask;
        hub.Sweep(_ => false, DateTime.UtcNow);
        Assert.Empty(hub.Sessions);
    }

    [Fact]
    public async Task Cursor_never_gets_a_permission_answer()
    {
        var hub = new AgentHub();
        foreach (var n in new[] { "afterFileEdit", "afterShellExecution", "postToolUse", "stop" })
            Assert.Equal("", await hub.HandleAsync(E(n, source: "cursor")));
        Assert.Equal("{\"continue\":true}", await hub.HandleAsync(E("beforeSubmitPrompt", source: "cursor")));
    }

    [Theory]
    [InlineData(true, false, false, NotchState.Hidden)]
    [InlineData(false, true, true, NotchState.Strip)]
    [InlineData(false, false, true, NotchState.Both)]
    [InlineData(false, false, false, NotchState.Agent)]
    public async Task Chooses_by_screen(bool full, bool covered, bool music, NotchState want)
    {
        var hub = new AgentHub();
        await hub.HandleAsync(E("UserPromptSubmit"));
        Assert.Equal(want, Presenter.Choose(hub, new Scene(full, covered, music, false, false)));
    }

    [Fact]
    public void Long_commands_are_never_cut_in_the_approval()
    {
        var cmd = new string('x', 5000);
        Assert.Equal(cmd, Describe.ApprovalBody("Bash", new JsonObject { ["command"] = cmd }));
    }
}
