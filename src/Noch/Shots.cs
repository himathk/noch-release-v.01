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
            var w = new NotchWindow(hub, media, new Settings()) { Left = -4000, Top = -4000, WindowStartupLocation = WindowStartupLocation.Manual };
            w.Show();
            w.Left = -4000;
            void Shot(string name, View v) { w.ShowForPicture(v); Save(w, Path.Combine(dir, name + ".png")); }

            Shot("01-rest", new RestView(w, false));
            Shot("02-music", new CompactView(w, false, true));
            _ = hub.HandleAsync(E("p", "UserPromptSubmit", @"D:\Work\portfolio"));
            _ = hub.HandleAsync(E("p", "PreToolUse", @"D:\Work\portfolio", "\"tool_name\":\"Edit\",\"tool_input\":{\"file_path\":\"src/auth.ts\"}"));
            Shot("03-agent", new CompactView(w, true, false));
            _ = hub.HandleAsync(E("p", "SubagentStart", @"D:\Work\portfolio", "\"agent_id\":\"a\""));
            _ = hub.HandleAsync(E("p", "SubagentStart", @"D:\Work\portfolio", "\"agent_id\":\"b\""));
            Shot("04-both", new CompactView(w, true, true));
            foreach (var (name, dock) in new[] { ("left", new Noch.Core.Dock(Side.Left, .5)), ("right", new Noch.Core.Dock(Side.Right, .3)) })
            {
                w.SetDockForPicture(dock);
                Shot("20-" + name, new CompactView(w, true, true));
            }
            foreach (var (name, dock) in new[] { ("bottom", new Noch.Core.Dock(Side.Bottom, .5)), ("corner-tl", new Noch.Core.Dock(Side.Top, 0)), ("corner-tr", new Noch.Core.Dock(Side.Top, 1)), ("corner-br", new Noch.Core.Dock(Side.Bottom, 1)) })
            {
                w.SetDockForPicture(dock);
                Shot("20-" + name, new CompactView(w, true, true));
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

    static void Save(NotchWindow w, string path)
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
