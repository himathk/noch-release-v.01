#if DEBUG
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Noch.Core;

namespace Noch;

/// Things that only work in a real Windows process, checked without touching anything the user owns.
static class Checks
{
    public static void Run(string report)
    {
        var sb = new StringBuilder();
        void Check(string name, Func<bool> f) { try { sb.AppendLine((f() ? "ok    " : "FAIL  ") + name); } catch (Exception e) { sb.AppendLine($"FAIL  {name}: {e.Message}"); } }
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            var dir = Directory.CreateTempSubdirectory("noch-check").FullName;
            var notices = new Notices();

            foreach (var t in Enum.GetValues<Sounds.Tone>())
                Check($"sound {t} is a valid, quiet, click-free wave", () =>
                {
                    var w = Sounds.Wave(t, .5);
                    short S(int i) => BitConverter.ToInt16(w, 44 + i * 2);
                    var n = (w.Length - 44) / 2;
                    var peak = Enumerable.Range(0, n).Max(i => Math.Abs((int)S(i)));
                    return Encoding.ASCII.GetString(w, 0, 4) == "RIFF" && BitConverter.ToInt32(w, 4) == w.Length - 8 && Math.Abs(S(0)) < 200 && Math.Abs(S(n - 1)) < 50 && peak is > 4000 and < 9000 && n < 44100 * 2;
                });
            Check("sounds at zero volume are silent", () => Sounds.Wave(Sounds.Tone.Finished, 0).Skip(44).All(b => b == 0));

            Check("scrolling changes the volume by a step, and it can be put back exactly", () =>
            {
                var before = Volume.Get();
                try { var to = Volume.Change(before > .5 ? -.02 : .02); return to is { } x && Math.Abs(Volume.Get() - x) < .011 && Math.Abs(x - before) < .031; }
                finally { Volume.Set(before); }
            });
            var power = new PowerWatch(notices, () => true);
            Check("no battery message at start", () => { power.Look(true, 90); return notices.Current is null; });
            Check("unplugging says so", () => { power.Look(false, 90); return notices.Current?.Title == "On battery"; });
            Check("low battery warns once per step", () =>
            {
                power.Look(false, 19); var a = notices.Current?.Title; notices.Dismiss("power");
                power.Look(false, 18); var b = notices.Current; power.Look(false, 10); return a == "Battery low" && b is null && notices.Current?.Detail == "10% left";
            });
            notices.Dismiss("power");
            Check("no battery at all is ignored", () => { power.Look(true, -1); return notices.Current is null; });

            var settings = new Settings();
            var cal = new CalendarService(settings, notices, new Sounds(settings));
            var now = DateTime.UtcNow;
            cal.Meetings = [new Meeting("a", "Standup", now.AddSeconds(50), now.AddMinutes(15), "https://meet.google.com/x"), new Meeting("b", "Later", now.AddHours(2), now.AddHours(3), null)];
            Check("a meeting is announced once, with Join", () =>
            {
                cal.Remind(now); var n = notices.Current; notices.Dismiss(n?.Id ?? ""); cal.Remind(now.AddSeconds(5));
                return n is { Kind: NoticeKind.Meeting, ActionLabel: "Join" } && notices.Current is null;
            });
            Check("the bar shows a meeting only when it is near", () => cal.Soon(now)?.Id == "a" && cal.Soon(now.AddMinutes(-30)) is null && cal.Next(now.AddMinutes(20))?.Id == "b");
            Check("bad calendar links are refused and not saved", () => cal.SetLink("http://x.com/a.ics") != null && cal.SetLink("hello") != null && settings.CalendarSecret is null);

            var f = Path.Combine(dir, "a file.txt"); File.WriteAllText(f, "x");
            Check("file pictures load", () => FileIcon.For(f, 96) is { PixelWidth: > 16 });
            Check("a missing file has no picture and no crash", () => FileIcon.For(Path.Combine(dir, "gone.txt"), 96) is null);
            Check("opening refuses anything that is not a web address or a real file", () => { AppServices.Open("javascript:1"); AppServices.Open("cmd.exe /c calc"); AppServices.Open(Path.Combine(dir, "gone")); return true; });
            Check("settings survive nonsense", () =>
            {
                var path = Path.Combine(Settings.Folder, "settings.json");
                if (Program.Name == "Noch") return false;
                Directory.CreateDirectory(Settings.Folder);
                File.WriteAllText(path, "{\"SoundVolume\":99,\"FocusMinutes\":-5,\"MutedApps\":null,\"Focus\":null,\"Dock\":\"Sideways:9\"}");
                var s = Settings.Load(); File.WriteAllText(path, "{ broken"); var t = Settings.Load(); File.Delete(path);
                return s.SoundVolume == 1 && s.FocusMinutes == 1 && s.MutedApps.Count == 0 && s.Focus != null && Noch.Core.Dock.Parse(s.Dock) == Noch.Core.Dock.TopCentre && t.FocusMinutes == 25;
            });

            // A real fetch of a public calendar, end to end.
            var live = new Settings();
            var feed = new CalendarService(live, notices, new Sounds(live));
            var done = new TaskCompletionSource();
            feed.Changed += () => { if (feed.Status is not "Checking…" and not "") done.TrySetResult(); };
            feed.SetLink("webcal://calendar.google.com/calendar/ical/en.uk%23holiday%40group.v.calendar.google.com/public/basic.ics");
            await Task.WhenAny(done.Task, Task.Delay(30000));
            sb.AppendLine((feed.Status.StartsWith("Connected") ? "ok    " : "FAIL  ") + "fetches a public calendar: " + feed.Status);
            Check("the link is not stored readably", () => live.CalendarSecret is { Length: > 20 } c && !c.Contains("google") && !File.ReadAllText(Path.Combine(Settings.Folder, "settings.json")).Contains("google"));
            feed.Forget();
            feed.SetLink("https://example.com/");
            done = new TaskCompletionSource();
            await Task.WhenAny(done.Task, Task.Delay(12000));
            sb.AppendLine((feed.Status.StartsWith("That address") || feed.Status.StartsWith("Connected. Nothing") ? "ok    " : "FAIL  ") + "a web page is not mistaken for a calendar: " + feed.Status);

            Directory.Delete(dir, true);
            File.WriteAllText(report, sb.ToString());
            app.Shutdown();
        };
        app.Run();
    }
}
#endif
