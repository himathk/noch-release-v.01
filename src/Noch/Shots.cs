#if DEBUG
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Noch.Core;

namespace Noch;

/// Draws every state to a PNG with made-up data, and draws the app icon. Debug builds only.
static class Shots
{
    static long _t = 1;
    static HookEvent E(string id, string name, string cwd, string extra = "", string source = "claude") =>
        new(source, _t++, [], (JsonObject)JsonNode.Parse($"{{\"session_id\":\"{id}\",\"cwd\":\"{cwd.Replace("\\", "\\\\")}\",\"hook_event_name\":\"{name}\"{(extra.Length > 0 ? "," + extra : "")}}}")!);

    public static void Run(string dir)
    {
        Directory.CreateDirectory(dir);
        DotMatrix.Pose();
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            var media = new MediaService();
            var art = Art();
            typeof(MediaService).GetProperty("Current")!.SetValue(media, new NowPlaying
            {
                Title = "Low Tide", Artist = "Mara Vell", Source = "Spotify", Playing = true, Position = TimeSpan.FromSeconds(96), Duration = TimeSpan.FromSeconds(241), At = DateTimeOffset.Now, CanNext = true, CanPrevious = true, CanSeek = true,
                Art = art, Tint = MediaService.TintOf(art)
            });
            var hub = new AgentHub { MinTurnForToast = TimeSpan.Zero };
            var settings = new Settings();
            var notices = new Notices();
            var sounds = new Sounds(settings);
            var quiet = new VoiceService(new Settings { Voice = false });
            var services = new AppServices { Settings = settings, Hub = hub, Media = media, Notices = notices, Sounds = sounds, Usage = new Usage(), Timer = new FocusTimer(settings.Focus), Shelf = new Shelf(), Stats = new Stats(), Calendar = new CalendarService(settings, notices, sounds), Notifications = new NotificationService(settings, notices), Voice = quiet, Listen = new ListenIn(settings, quiet) };
            var w = new NotchWindow(services) { Left = -4000, Top = -4000, WindowStartupLocation = WindowStartupLocation.Manual };
            w.Show();
            w.Left = -4000;
            void Shot(string name, View v) { w.ShowForPicture(v); Save(w, Path.Combine(dir, name + ".png")); }

            Shot("01-rest", new RestView(w, false));
            Shot("02-music", new CompactView(w, Show.Music));
            _ = hub.HandleAsync(E("p", "UserPromptSubmit", @"D:\Work\portfolio"));
            _ = hub.HandleAsync(E("p", "PreToolUse", @"D:\Work\portfolio", "\"tool_name\":\"Edit\",\"tool_input\":{\"file_path\":\"src/auth.ts\"}"));
            Shot("03-agent", new CompactView(w, Show.Agent));
            _ = hub.HandleAsync(E("p", "SubagentStart", @"D:\Work\portfolio", "\"agent_id\":\"a\""));
            _ = hub.HandleAsync(E("p", "SubagentStart", @"D:\Work\portfolio", "\"agent_id\":\"b\""));
            Shot("04-both", new CompactView(w, Show.Agent | Show.Music));
            foreach (var (name, dock) in new[] { ("left", new Noch.Core.Dock(Side.Left, .5)), ("right", new Noch.Core.Dock(Side.Right, .3)) })
            {
                w.SetDockForPicture(dock);
                Shot("20-" + name, new CompactView(w, Show.Agent | Show.Music));
            }
            foreach (var (name, dock) in new[] { ("bottom", new Noch.Core.Dock(Side.Bottom, .5)), ("corner-tl", new Noch.Core.Dock(Side.Top, 0)), ("corner-tr", new Noch.Core.Dock(Side.Top, 1)), ("corner-br", new Noch.Core.Dock(Side.Bottom, 1)) })
            {
                w.SetDockForPicture(dock);
                Shot("20-" + name, new CompactView(w, Show.Agent | Show.Music));
            }
            w.SetDockForPicture(Noch.Core.Dock.TopCentre);
            Film(Path.Combine(dir, "30-drag.png"));
            _ = hub.HandleAsync(E("c", "postToolUse", @"D:\Work\noch", "\"tool_name\":\"Shell\",\"tool_input\":{\"command\":\"npm test\",\"description\":\"Running tests\"}", "cursor"));
            _ = hub.HandleAsync(E("i", "PermissionRequest", @"D:\Work\shop", "\"tool_name\":\"Bash\",\"tool_input\":{\"command\":\"npx prisma migrate deploy\",\"description\":\"Migrate the orders tables\"},\"permission_suggestions\":[{\"type\":\"addRules\",\"rules\":[{\"toolName\":\"Bash\",\"ruleContent\":\"npx prisma migrate:*\"}],\"behavior\":\"allow\",\"destination\":\"localSettings\"}]"));
            foreach (var (k, sec) in new[] { ("claude:i", 8), ("claude:p", 102), ("cursor:c", 31) })
                typeof(AgentSession).GetProperty("TurnStarted")!.SetValue(hub.Find(k), DateTime.UtcNow.AddSeconds(-sec));
            Shot("05-strip", new StripView(w));
            Shot("06-approval", new ApprovalView(w, hub.Pending[0]));
            Shot("07-peek", new PeekView(w));
            Shot("08-player", new PlayerView(w));
            _ = hub.HandleAsync(E("e", "PermissionRequest", @"D:\Work\shop", "\"tool_name\":\"Edit\",\"tool_input\":{\"file_path\":\"D:\\\\Work\\\\shop\\\\cart.ts\",\"old_string\":\"const tax = 0.1\",\"new_string\":\"const tax = region.tax\\nconst total = net * (1 + tax)\"}"));
            Shot("09-edit", new ApprovalView(w, hub.Pending[1]));
            _ = hub.HandleAsync(E("q", "PreToolUse", @"D:\Work\shop", "\"tool_name\":\"AskUserQuestion\",\"tool_input\":{\"questions\":[{\"question\":\"Which sign-in should I use?\",\"header\":\"Auth\",\"multiSelect\":false,\"options\":[{\"label\":\"Clerk\",\"description\":\"Hosted, quickest to add\"},{\"label\":\"Auth.js\",\"description\":\"Self-hosted, more setup\"},{\"label\":\"Keep the current one\",\"description\":\"No change\"}]}]}"));
            Shot("10-question", new QuestionView(w, hub.Pending[2]));
            Shot("11-done", new ToastView(w, new Toast("p", false, "Claude Code finished", "portfolio, took 4:12", DateTime.UtcNow)));
            Shot("12-failed", new ToastView(w, new Toast("c", true, "Cursor stopped", "noch, something went wrong", DateTime.UtcNow)));
            // ---- newer screens
            var now = DateTime.UtcNow;
            services.Timer.Start(TimeSpan.FromMinutes(25), false, now.AddMinutes(-9.4));
            services.Calendar.Meetings = [new Meeting("m", "Design review", now.AddMinutes(7.5), now.AddMinutes(37), "https://meet.google.com/abc")];
            Shot("40-timer", new CompactView(w, Show.Timer));
            Shot("41-everything", new CompactView(w, Show.Agent | Show.Music | Show.Timer | Show.Meeting));
            Shot("42-meeting", new CompactView(w, Show.Meeting | Show.Music));
            Shot("43-focus", new FocusView(w));
            services.Timer.Pause(now);
            Shot("44-paused", new FocusView(w));
            Shot("45-shelf-empty", new ShelfView(w));
            var sample = Path.Combine(Path.GetTempPath(), "noch-sample"); Directory.CreateDirectory(sample);
            foreach (var f in new[] { "Invoice March.pdf", "logo-final-v2.png", "notes.txt", "archive.zip" }) File.WriteAllText(Path.Combine(sample, f), "x");
            services.Shelf.Add(Directory.GetFiles(sample).Select(f => new ShelfItem(ShelfKind.File, f)).Append(Shelf.FromText("https://figma.com/design/abc")!).Append(Shelf.FromText("Remember to rename the branch")!));
            var shelf = new ShelfView(w);
            w.ShowForPicture(shelf);
            await Task.Delay(700);
            Shot("46-shelf", shelf);
            Shot("47-meeting-now", new NoticeView(w, new Notice("a", NoticeKind.Meeting, "Design review", "Starts in a minute") { ActionLabel = "Join" }));
            Shot("48-app", new NoticeView(w, new Notice("b", NoticeKind.App, "Nimal", "WhatsApp") { Body = "Are we still on for lunch? I can do half past twelve or one, whichever suits you better.", ActionLabel = "Open", SideLabel = "Mute" }));
            Shot("49-clash", new NoticeView(w, new Notice("c", NoticeKind.Warning, "Two agents are editing cart.ts", "shop and shop-mobile") { ActionLabel = "Open" }));
            Shot("50-volume", new NoticeView(w, new Notice("d", NoticeKind.Volume, "Volume") { Level = .42 }));
            Shot("51-charging", new NoticeView(w, new Notice("e", NoticeKind.Power, "Charging", "64%") { Level = .64 }));
            Shot("52-done-summary", new ToastView(w, new Toast("claude:p", false, "Claude Code finished", "portfolio, took 4:12", now) { Body = "Added sign-in with Auth.js and moved the session check into middleware. All 48 tests pass. One thing left for you: add the two secrets to your .env file." }));
            Shot("53-timer-done", new NoticeView(w, new Notice("f", NoticeKind.Timer, "Focus done", "Nice work.") { ActionLabel = "Break 5 min" }));
            var later = new DateTimeOffset(now.AddHours(2)).ToUnixTimeSeconds();
            services.Usage.Take((JsonObject)JsonNode.Parse("{\"session_id\":\"p\",\"context_window\":{\"used_percentage\":31},\"rate_limits\":{\"five_hour\":{\"used_percentage\":42,\"resets_at\":" + later + "},\"seven_day\":{\"used_percentage\":93,\"resets_at\":" + (later + 300000) + "}}}")!, now);
            services.Stats.Turn(TimeSpan.FromMinutes(100), DateTime.Now); services.Stats.Focused(50, DateTime.Now);
            if (hub.Find("claude:i") is { } si) si.ContextTokens = 182_000;
            services.Timer.Resume(now);
            Shot("54-peek-full", new PeekView(w));
            w.SetDockForPicture(new Noch.Core.Dock(Side.Left, .5));
            Shot("55-left-everything", new CompactView(w, Show.Agent | Show.Music | Show.Timer | Show.Meeting));
            w.SetDockForPicture(Noch.Core.Dock.TopCentre);
            services.Timer.Stop();
            var sw = new SettingsWindow(services, new FakeActions());
            sw.Left = -3000; sw.WindowStartupLocation = WindowStartupLocation.Manual; sw.Height = 1700; sw.Show(); sw.UpdateLayout();
            await Task.Delay(300);
            Write((Visual)VisualTreeHelper.GetChild(sw, 0), (int)(sw.ActualWidth * 1.5), (int)(sw.ActualHeight * 1.5), 144, Path.Combine(dir, "60-settings.png"));
            sw.Close();
            w.SetupItems = () => [new("Claude Code", "Show its work and answer it from here", "Connect", () => { }), new("Cursor", "Show what it is doing", "Connect", () => { })];
            foreach (var k in new[] { "p", "i", "e", "q" }) _ = hub.HandleAsync(E(k, "SessionEnd", ""));
            _ = hub.HandleAsync(E("c", "sessionEnd", "", source: "cursor"));
            Shot("13-welcome", new PeekView(w));
            await Task.Delay(50);
            app.Shutdown();
        };
        app.Run();
    }

    /// A scripted pull off the top edge, break, carry and drop on the right edge, drawn as a strip of frames.
    static void Film(string path)
    {
        var area = new Size(760, 330);
        var bubble = new Bubble(Noch.Core.Dock.TopCentre, new Box(230, 0, 300, 36), area, 380, 18);
        var frames = new List<(double T, Action<DrawingContext> Draw)>();
        var dv = new DrawingVisual();
        double[] at = [0, .05, .1, .16, .22, .27, .3, .34, .39, .45, .55, .84, .92, .97, 1.03, 1.1];
        const int cols = 4; const double k = .6;
        int cell = 0; var t = 0.0;
        using (var dc = dv.RenderOpen())
        {
            foreach (var stop in at)
            {
                for (; t < stop && !bubble.Landed; t += 1 / 120.0)
                {
                    // pull down, carry right, let go at 0.9 s
                    var p = t < .4 ? new Point(380, 18 + t / .4 * 200) : new Point(380 + Math.Min(1, (t - .4) / .45) * 300, 200);
                    if (t > .9) bubble.Release();
                    bubble.Step(1 / 120.0, p.X, p.Y);
                }
                double x = cell % cols * (area.Width + 24), y = cell / cols * (area.Height + 24); cell++;
                dc.PushClip(new RectangleGeometry(new Rect(x, y, area.Width, area.Height)));
                dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x79, 0xA9, 0xCF), Color.FromRgb(0xC2, 0xDB, 0xE6), 90), null, new Rect(x, y, area.Width, area.Height));
                foreach (var side in Enum.GetValues<Side>()) bubble.DrawEdge(dc, side, -x, -y);
                bubble.DrawBall(dc, -x, -y);
                dc.DrawText(new FormattedText($"{stop * 1000:0} ms{(bubble.Landed ? " landed" : "")}", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 26, Brushes.Black, 1), new Point(x + 16, y + area.Height - 46));
                dc.Pop();
            }
        }
        var root = new DrawingVisual();
        using (var dc = root.RenderOpen()) { dc.PushTransform(new ScaleTransform(k, k)); dc.DrawDrawing(dv.Drawing); }
        Write(root, (int)(cols * (area.Width + 24) * k), (int)((at.Length / cols) * (area.Height + 24) * k), 96, path);
    }

    sealed class FakeActions : SettingsWindow.ISettingsActions
    {
        static readonly string Dir = Path.Combine(Path.GetTempPath(), "noch-sample");
        public ClaudeConnector Claude { get; } = new(Dir, "noch-hook.exe");
        public IReadOnlyList<(string, Connector)> Agents => [("Claude Code", Claude), ("Cursor", new CursorConnector(Dir, "noch-hook.exe")), ("Codex", new CodexConnector(Path.Combine(Dir, "none"), "noch-hook.exe"))];
        public string? Try(Action a) => null;
        public void Refresh() { }
        public string Version => "0.3.1";
        public void CheckForUpdates(Action<string> tell) { }
        public string? Repo => "https://github.com/himathk/noch-release-v.01";
    }

    internal static void Save(NotchWindow w, string path)
    {
        const double k = 2, pad = 20;
        // Pad every side except the ones the notch is attached to.
        var b = w.ShellBody; var d = w.ShellDock; var ear = w.ShellEar;
        double l = d.Side == Side.Left || d.AtStart ? 0 : pad + ear, r = d.Side == Side.Right || d.AtEnd ? 0 : pad + ear, t = d.Side == Side.Top ? 0 : pad + (d.Vertical ? ear : 0), o = d.Side == Side.Bottom ? 0 : pad + (d.Vertical || d.Corner ? ear : 0);
        var box = new Size(b.W + l + r, b.H + t + o);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x79, 0xA9, 0xCF), Color.FromRgb(0xC2, 0xDB, 0xE6), 90), null, new Rect(box));
            dc.DrawRectangle(new VisualBrush(w.ShellElement) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top, ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(b.X - l, b.Y - t, box.Width, box.Height) }, null, new Rect(box));
        }
        Write(dv, (int)(box.Width * k), (int)(box.Height * k), 96 * k, path);
    }

    static byte[] Write(Visual v, int w, int h, double dpi, string? path)
    {
        var b = new RenderTargetBitmap(w, h, dpi, dpi, PixelFormats.Pbgra32);
        b.Render(v);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(b));
        using var ms = new MemoryStream(); png.Save(ms);
        if (path != null) File.WriteAllBytes(path, ms.ToArray());
        return ms.ToArray();
    }

    static BitmapSource Art()
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0xFF, 0x9E, 0x6B), Color.FromRgb(0x3B, 0x14, 0x20), 90), null, new Rect(0, 0, 216, 216));
            dc.DrawEllipse(Theme.Brush(Color.FromRgb(0xFF, 0xE3, 0xB8)), null, new Point(108, 92), 50, 50);
            for (var i = 0; i < 4; i++) dc.DrawRectangle(Theme.Brush(Color.FromRgb(0x7A, 0x22, 0x30)), null, new Rect(0, 117 + i * 24, 216, 9 + i * 4));
        }
        var b = new RenderTargetBitmap(216, 216, 96, 96, PixelFormats.Pbgra32); b.Render(dv); b.Freeze();
        return b;
    }

    public static void Icon(string path)
    {
        _ = new Application();
        var sizes = new[] { 16, 20, 24, 32, 48, 64, 128, 256 };
        var images = sizes.Select(s =>
        {
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                // A notch hanging from the top edge, with the thinking ripple inside it.
                dc.DrawRoundedRectangle(Theme.Surface, null, new Rect(0, 0, s, s), s * .24, s * .24);
                var small = s <= 24; var n = small ? 3 : 5; var pitch = s * (small ? .27 : .15); var r = s * (small ? .1 : .055);
                for (var y = 0; y < n; y++)
                    for (var x = 0; x < n; x++)
                    {
                        var d = Math.Abs(x - n / 2) + Math.Abs(y - n / 2);
                        var lit = small ? d == 1 : d == 2; var mid = d == 0;
                        dc.DrawEllipse(Theme.Brush(mid ? Theme.NeedColor : Theme.TextColor, mid || lit ? 1 : .25), null, new Point(s / 2.0 + (x - n / 2) * pitch, s / 2.0 + (y - n / 2) * pitch), r, r);
                    }
            }
            return Write(dv, s, s, 96, null);
        }).ToArray();
        using var f = new BinaryWriter(File.Create(path));
        f.Write((short)0); f.Write((short)1); f.Write((short)sizes.Length);
        var at = 6 + 16 * sizes.Length;
        for (var i = 0; i < sizes.Length; i++)
        {
            f.Write((byte)(sizes[i] % 256)); f.Write((byte)(sizes[i] % 256)); f.Write((short)0); f.Write((short)1); f.Write((short)32); f.Write(images[i].Length); f.Write(at);
            at += images[i].Length;
        }
        foreach (var i in images) f.Write(i);
    }
}
#endif
