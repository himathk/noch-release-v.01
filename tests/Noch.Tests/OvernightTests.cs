using System.Text.Json.Nodes;
using Noch.Core;
using Xunit;

namespace Noch.Tests;

public sealed class OvernightTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("noch").FullName;
    public void Dispose() => Directory.Delete(_dir, true);
    static readonly DateTime T0 = new(2026, 11, 25, 9, 0, 0, DateTimeKind.Utc);

    long _t = 1;
    HookEvent E(string id, string name, string extra = "", string source = "claude", string cwd = "D:/Work/ict") =>
        new(source, _t++, [1], (JsonObject)JsonNode.Parse($$"""{"session_id":"{{id}}","cwd":"{{cwd}}","hook_event_name":"{{name}}"{{(extra.Length > 0 ? "," + extra : "")}}}""")!, 1);
    static string Edit(string path) => $"\"tool_name\":\"Edit\",\"tool_input\":{{\"file_path\":\"{path}\"}}";

    // ---- timer ----

    [Fact]
    public void Timer_counts_down_pauses_and_finishes()
    {
        var t = new FocusTimer(new FocusState());
        bool? ended = null;
        t.Finished += b => ended = b;
        t.Start(TimeSpan.FromMinutes(25), false, T0);
        Assert.True(t.Quiet);
        Assert.Equal(TimeSpan.FromMinutes(20), t.Left(T0.AddMinutes(5)));
        t.Pause(T0.AddMinutes(5));
        Assert.False(t.Quiet);
        Assert.Equal(TimeSpan.FromMinutes(20), t.Left(T0.AddHours(3)));
        t.Resume(T0.AddHours(3));
        t.Add(TimeSpan.FromMinutes(5), T0.AddHours(3));
        Assert.Equal(1, t.Fraction(T0.AddHours(3)), 3);
        t.Tick(T0.AddHours(3).AddMinutes(24));
        Assert.Null(ended);
        t.Tick(T0.AddHours(3).AddMinutes(25));
        Assert.False(ended);
        Assert.False(t.Active);
    }

    [Fact]
    public void Timer_that_ended_while_asleep_says_nothing()
    {
        var state = new FocusState { EndsAt = T0, Length = 60 };
        var t = new FocusTimer(state);
        var fired = false;
        t.Finished += _ => fired = true;
        t.Tick(T0.AddHours(8));
        Assert.False(fired);
        Assert.False(t.Active);
    }

    [Fact]
    public void Timer_stays_within_bounds()
    {
        var t = new FocusTimer(new FocusState());
        t.Start(TimeSpan.FromHours(99), false, T0);
        Assert.Equal(FocusTimer.Longest, t.Left(T0));
        t.Add(TimeSpan.FromHours(-99), T0);
        Assert.True(t.Left(T0) > TimeSpan.Zero);
    }

    // ---- shelf ----

    [Fact]
    public void Shelf_keeps_order_skips_doubles_and_survives_restart()
    {
        var f = Path.Combine(_dir, "shelf.json");
        var s = new Shelf(f);
        Assert.Equal(2, s.Add([new(ShelfKind.File, @"C:\a.txt"), new(ShelfKind.File, @"C:\b.txt")]));
        Assert.Equal(0, s.Add([new(ShelfKind.File, @"c:\A.TXT")]));
        s.Add([Shelf.FromText(" https://example.com/x ")!, Shelf.FromText("a note")!]);
        var again = new Shelf(f);
        Assert.Equal([ShelfKind.Text, ShelfKind.Link, ShelfKind.File, ShelfKind.File], again.Items.Select(i => i.Kind));
        again.Prune(p => p.EndsWith("a.txt"));
        Assert.Equal(3, again.Items.Count);
        again.Clear();
        Assert.Empty(new Shelf(f).Items);
    }

    [Theory]
    [InlineData("https://a.com", ShelfKind.Link)]
    [InlineData("ftp://a.com", ShelfKind.Text)]
    [InlineData("javascript:alert(1)", ShelfKind.Text)]
    [InlineData("hello", ShelfKind.Text)]
    public void Only_web_addresses_become_links(string text, ShelfKind kind) => Assert.Equal(kind, Shelf.FromText(text)!.Kind);

    [Fact]
    public void Shelf_has_a_limit_and_ignores_a_broken_file()
    {
        var f = Path.Combine(_dir, "shelf.json");
        File.WriteAllText(f, "[{\"Kind\":99,\"Value\":\"x\"},null,{\"Kind\":0}]");
        var s = new Shelf(f);
        Assert.Empty(s.Items);
        s.Add(Enumerable.Range(0, 60).Select(i => new ShelfItem(ShelfKind.File, $@"C:\{i}")));
        Assert.Equal(Shelf.Max, s.Items.Count);
        Assert.Equal(@"C:\59", s.Items[0].Value);
    }

    // ---- calendar ----

    static string Ics(string events) => "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:x\r\n" + events.Replace("\r", "").Replace("\n", "\r\n") + "\r\nEND:VCALENDAR\r\n";

    [Fact]
    public void Reads_plain_repeating_and_moved_meetings()
    {
        var ics = Ics("""
            BEGIN:VEVENT
            UID:one
            DTSTART:20261125T093000Z
            DTEND:20261125T100000Z
            SUMMARY:Design review
            LOCATION:https://meet.google.com/abc-defg-hij
            END:VEVENT
            BEGIN:VEVENT
            UID:daily
            DTSTART;TZID=Asia/Colombo:20261101T160000
            DTEND;TZID=Asia/Colombo:20261101T161500
            RRULE:FREQ=DAILY
            EXDATE;TZID=Asia/Colombo:20261126T160000
            SUMMARY:Standup
            END:VEVENT
            BEGIN:VEVENT
            UID:allday
            DTSTART;VALUE=DATE:20261125
            SUMMARY:Holiday
            END:VEVENT
            BEGIN:VEVENT
            UID:off
            STATUS:CANCELLED
            DTSTART:20261125T110000Z
            DTEND:20261125T113000Z
            SUMMARY:Cancelled
            END:VEVENT
            """);
        var list = Agenda.Parse(ics, T0, T0.AddDays(3));
        Assert.Equal(["Design review", "Standup", "Standup", "Standup"], list.Select(m => m.Title).Append("Standup").Take(4));
        Assert.Equal("https://meet.google.com/abc-defg-hij", list[0].Link);
        // 16:00 in Colombo is 10:30 UTC. The 26th is skipped.
        Assert.Equal([new DateTime(2026, 11, 25, 10, 30, 0), new DateTime(2026, 11, 27, 10, 30, 0)], list.Where(m => m.Title == "Standup").Select(m => m.Start));
    }

    [Fact]
    public void A_meeting_already_under_way_still_counts()
    {
        var list = Agenda.Parse(Ics("BEGIN:VEVENT\nUID:a\nDTSTART:20261125T084500Z\nDTEND:20261125T091500Z\nSUMMARY:Now\nEND:VEVENT"), T0, T0.AddDays(1));
        Assert.Equal("now", list.Single().When(T0));
    }

    [Theory]
    [InlineData("Join https://us02web.zoom.us/j/123?pwd=a.", "https://us02web.zoom.us/j/123?pwd=a")]
    [InlineData("<https://teams.microsoft.com/l/meetup-join/x>", "https://teams.microsoft.com/l/meetup-join/x")]
    [InlineData("https://evil.com/zoom.us", null)]
    [InlineData("https://notzoom.us/j/1", null)]
    [InlineData("http://zoom.us/j/1", null)]
    [InlineData("Room 4", null)]
    public void Finds_only_real_call_links(string text, string? want) => Assert.Equal(want, Agenda.Link(text));

    [Theory]
    [InlineData("webcal://x.com/a.ics", "https://x.com/a.ics")]
    [InlineData(" https://x.com/a.ics ", "https://x.com/a.ics")]
    [InlineData("http://x.com/a.ics", null)]
    [InlineData("file:///c:/a.ics", null)]
    [InlineData("hello", null)]
    public void Accepts_only_secure_feeds(string text, string? want) => Assert.Equal(want, Agenda.Feed(text)?.AbsoluteUri);

    [Theory]
    [InlineData(24, "in 24 min")]
    [InlineData(60, "in 1 h")]
    [InlineData(125, "in 2 h 5 min")]
    public void Says_when(int minutes, string want) => Assert.Equal(want, new Meeting("a", "b", T0.AddMinutes(minutes), T0.AddMinutes(minutes + 30), null).When(T0));

    // ---- usage ----

    [Fact]
    public void Status_line_gives_limits_and_they_expire()
    {
        var u = new Usage();
        var reset = new DateTimeOffset(T0.AddHours(2)).ToUnixTimeSeconds();
        var line = u.Take((JsonObject)JsonNode.Parse("""{"session_id":"a","context_window":{"used_percentage":31.4},"rate_limits":{"five_hour":{"used_percentage":42,"resets_at":RESET}}}""".Replace("RESET", reset.ToString()))!, T0);
        Assert.Equal("context 31%   5 h 42%", line);
        Assert.Equal(42, u.FiveHour(T0)!.Percent);
        Assert.Null(u.SevenDay(T0));
        Assert.Equal(31.4, u.Context("claude:a"));
        Assert.Null(u.FiveHour(T0.AddHours(3)));
        Assert.Equal("", new Usage().Take((JsonObject)JsonNode.Parse("""{"context_window":{"used_percentage":null},"rate_limits":7}""")!, T0));
    }

    [Fact]
    public void Transcript_gives_the_latest_main_conversation_size()
    {
        var f = Path.Combine(_dir, "t.jsonl");
        File.WriteAllText(f, """
            {"type":"assistant","isSidechain":false,"message":{"usage":{"input_tokens":1,"cache_read_input_tokens":1000,"cache_creation_input_tokens":20,"output_tokens":5}}}
            {"type":"assistant","isSidechain":true,"message":{"usage":{"input_tokens":9,"cache_read_input_tokens":9}}}
            {"type":"user","message":"mentions \"usage\" and \"assistant\""}
            not json "usage" "assistant"
            """);
        Assert.Equal(1026, Transcript.ContextTokens(f));
        Assert.Null(Transcript.ContextTokens(Path.Combine(_dir, "missing.jsonl")));
        Assert.Null(Transcript.ContextTokens(@"C:\Windows\win.ini"));
    }

    // ---- agents ----

    [Fact]
    public async Task Warns_once_when_two_agents_change_the_same_file()
    {
        var hub = new AgentHub();
        var hits = new List<string>();
        hub.Collided += (p, a, b) => hits.Add($"{Path.GetFileName(p)} {a.Key} {b.Key}");
        await hub.HandleAsync(E("a", "PreToolUse", Edit("D:/Work/ict/cart.ts")));
        await hub.HandleAsync(E("a", "PreToolUse", Edit("D:/Work/ict/cart.ts")));
        Assert.Empty(hits);
        await hub.HandleAsync(E("b", "PreToolUse", Edit(@"d:\\work\\ict\\CART.ts")));
        await hub.HandleAsync(E("a", "PreToolUse", Edit("cart.ts")));
        Assert.Equal(["CART.ts claude:a claude:b"], hits);
        await hub.HandleAsync(E("c", "PreToolUse", Edit("D:/Work/ict-copy/cart.ts")));
        Assert.Single(hits);
    }

    [Fact]
    public async Task Finishing_carries_a_readable_summary()
    {
        var hub = new AgentHub { MinTurnForToast = TimeSpan.Zero };
        var got = new List<Signal>();
        hub.Signalled += got.Add;
        await hub.HandleAsync(E("a", "UserPromptSubmit"));
        await hub.HandleAsync(E("a", "Stop", "\"last_assistant_message\":\"## Done\\n\\nFixed **login** in `auth.ts`.\\n\\n```ts\\ncode()\\n```\\n- See [docs](http://x)\""));
        Assert.Equal("Done. Fixed login in auth.ts. See docs.", hub.Toast!.Body);
        Assert.Equal([Signal.Finished], got);
    }

    [Theory]
    [InlineData("snake_case and 2*3*4 stay", "snake_case and 2*3*4 stay.")]
    [InlineData("*really* fine", "really fine.")]
    [InlineData("", "")]
    [InlineData("| a | b |\n|---|---|\n---\nText", "Text.")]
    public void Summaries_drop_formatting_only(string md, string want) => Assert.Equal(want, Describe.Summary(md));

    [Fact]
    public async Task Codex_is_followed_and_answered_like_claude()
    {
        var hub = new AgentHub();
        var a = hub.HandleAsync(E("x", "PermissionRequest", "\"tool_name\":\"Bash\",\"tool_input\":{\"command\":\"dir\",\"description\":null}", "codex"));
        Assert.Equal("Codex", hub.Sessions[0].Agent);
        Assert.Null(hub.Pending[0].Suggestions);
        hub.Deny(hub.Pending[0]);
        Assert.Equal("deny", (string?)JsonNode.Parse(await a)!["hookSpecificOutput"]!["decision"]!["behavior"]);
    }

    [Fact]
    public void Codex_and_status_line_settings_come_and_go_cleanly()
    {
        var x = new CodexConnector(_dir, @"C:\a b\noch-hook.exe");
        x.Connect();
        Assert.True(x.Connected);
        x.Disconnect();
        Assert.False(x.Connected);

        var c = new ClaudeConnector(_dir, @"C:\a b\noch-hook.exe");
        File.WriteAllText(c.ConfigPath, """{"model":"opus"}""");
        c.SetStatusLine(true);
        Assert.True(c.StatusLineOn);
        Assert.Equal("\"C:/a b/noch-hook.exe\" status", (string?)JsonNode.Parse(File.ReadAllText(c.ConfigPath))!["statusLine"]!["command"]);
        c.Connect();
        Assert.True(c.StatusLineOn);
        c.Disconnect();
        Assert.Equal("""{"model":"opus"}""", JsonNode.Parse(File.ReadAllText(c.ConfigPath))!.ToJsonString());

        File.WriteAllText(c.ConfigPath, """{"statusLine":{"type":"command","command":"mine"}}""");
        Assert.True(c.HasOtherStatusLine);
        Assert.Throws<InvalidOperationException>(() => c.SetStatusLine(true));
        Assert.Throws<InvalidOperationException>(() => c.SetStatusLine(false));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"questions\":[]}")]
    [InlineData("{\"questions\":[{\"question\":7,\"options\":\"no\",\"multiSelect\":\"yes\"}]}")]
    [InlineData("{\"questions\":\"x\"}")]
    public async Task Unreadable_questions_go_to_the_terminal_unanswered(string input)
    {
        var hub = new AgentHub();
        Assert.Equal("", await hub.HandleAsync(E("a", "PreToolUse", "\"tool_name\":\"AskUserQuestion\",\"tool_input\":" + input)));
        Assert.Empty(hub.Pending);
        Assert.Equal(Activity.Attention, hub.Sessions[0].Activity);
    }

    [Fact]
    public void Hidden_characters_in_a_command_are_shown()
    {
        var hub = new AgentHub();
        _ = hub.HandleAsync(E("a", "PermissionRequest", "\"tool_name\":\"Bash\",\"tool_input\":{\"command\":\"echo \\u202Esafe\\u200B\\u001b[2K\\nsecond\\tline é 😀\"}"));
        Assert.Equal("echo [U+202E]safe[U+200B][U+001B][2K\nsecond\tline é 😀", hub.Pending[0].Command);
    }

    [Fact]
    public async Task A_finished_agent_clicked_away_stays_away_until_it_works_again()
    {
        var hub = new AgentHub { MinTurnForToast = TimeSpan.Zero };
        await hub.HandleAsync(E("a", "UserPromptSubmit"));
        await hub.HandleAsync(E("a", "Stop", "\"last_assistant_message\":\"All done.\""));
        Assert.NotNull(hub.Toast);
        hub.Clear("claude:a");
        Assert.Null(hub.Toast);
        Assert.True(hub.Sessions[0].Cleared);
        // Its idle reminder a minute later does not bring it back.
        await hub.HandleAsync(E("a", "Notification", "\"notification_type\":\"idle_prompt\""));
        Assert.True(hub.Sessions[0].Cleared);
        await hub.HandleAsync(E("a", "UserPromptSubmit"));
        Assert.False(hub.Sessions[0].Cleared);
    }

    [Fact]
    public async Task One_that_is_working_or_waiting_cannot_be_cleared()
    {
        var hub = new AgentHub();
        await hub.HandleAsync(E("a", "UserPromptSubmit"));
        hub.Clear("claude:a");
        Assert.False(hub.Sessions[0].Cleared);
        _ = hub.HandleAsync(E("b", "PermissionRequest", "\"tool_name\":\"Bash\",\"tool_input\":{\"command\":\"ls\"}"));
        hub.Clear("claude:b");
        Assert.DoesNotContain(hub.Sessions, s => s.Cleared);
        hub.Clear("nobody");
    }

    // ---- what is on screen ----

    [Fact]
    public async Task Focus_quiets_chatter_but_not_what_matters()
    {
        var hub = new AgentHub { MinTurnForToast = TimeSpan.Zero };
        await hub.HandleAsync(E("a", "UserPromptSubmit"));
        await hub.HandleAsync(E("a", "Stop"));
        var quiet = new Scene(false, false, Panel.None, Timer: true, Quiet: true);
        Assert.Equal(NotchState.Compact, Presenter.Choose(hub, quiet));
        Assert.Equal(NotchState.Compact, Presenter.Choose(hub, quiet with { Notice = new("n", NoticeKind.App, "Chat") }));
        Assert.Equal(NotchState.Notice, Presenter.Choose(hub, quiet with { Notice = new("n", NoticeKind.Meeting, "Standup") }));
        Assert.Equal(NotchState.Notice, Presenter.Choose(hub, quiet with { Notice = new("n", NoticeKind.Warning, "Clash") }));
        _ = hub.HandleAsync(E("a", "PermissionRequest", "\"tool_name\":\"Bash\",\"tool_input\":{\"command\":\"x\"}"));
        Assert.Equal(NotchState.Approval, Presenter.Choose(hub, quiet));
    }

    [Fact]
    public void Notices_wait_their_turn_and_hold_under_the_pointer()
    {
        var n = new Notices();
        n.Post(new("a", NoticeKind.App, "first"));
        n.Post(new("m", NoticeKind.Meeting, "urgent") { Life = TimeSpan.FromSeconds(3) });
        Assert.Equal("m", n.Current!.Id);
        n.Tick(T0, true, false);
        n.Tick(T0.AddSeconds(4), true, true);
        n.Tick(T0.AddSeconds(60), true, true);
        Assert.Equal("m", n.Current!.Id);
        n.Tick(T0.AddSeconds(61), true, false);
        Assert.Equal("a", n.Current!.Id);
        for (var i = 0; i < 20; i++) n.Post(new("x" + i, NoticeKind.App, "x"));
        n.Post(new("v", NoticeKind.Volume, "v"));
        Assert.Equal("v", n.Current!.Id);
        // Covered by something else for a long while: not worth showing any more.
        n.Tick(DateTime.UtcNow.AddMinutes(3), false, false);
        Assert.Null(n.Current);
    }

    [Fact]
    public void Stats_add_up_and_survive_a_restart()
    {
        var f = Path.Combine(_dir, "stats.json");
        var s = new Stats(f);
        Assert.Equal("", s.Line(T0));
        s.Turn(TimeSpan.FromMinutes(100), T0);
        s.Turn(TimeSpan.FromSeconds(10), T0);
        s.Focused(25, T0);
        Assert.Equal("Today: 2 turns, 1 h 40 min of agent work, 25 min focused", new Stats(f).Line(T0));
        Assert.Equal("", s.Line(T0.AddDays(1)));
    }
}
