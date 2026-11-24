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

    static void Save(NotchWindow w, string path)
    {
        const double k = 2, pad = 20;
        var size = w.ShellSize;
        var box = new Size(size.Width + pad * 2, size.Height + pad);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x79, 0xA9, 0xCF), Color.FromRgb(0xC2, 0xDB, 0xE6), 90), null, new Rect(box));
            dc.DrawRectangle(new VisualBrush(w.ShellElement) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top, ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(w.Width / 2 - box.Width / 2, 0, box.Width, box.Height) }, null, new Rect(box));
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
