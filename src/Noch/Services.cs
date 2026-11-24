using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Noch.Core;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;
using Forms = System.Windows.Forms;

namespace Noch;

/// Everything the notch can show, in one place.
sealed class AppServices
{
    public required Settings Settings { get; init; }
    public required AgentHub Hub { get; init; }
    public required MediaService Media { get; init; }
    public required FocusTimer Timer { get; init; }
    public required Shelf Shelf { get; init; }
    public required Notices Notices { get; init; }
    public required Usage Usage { get; init; }
    public required Stats Stats { get; init; }
    public required CalendarService Calendar { get; init; }
    public required NotificationService Notifications { get; init; }
    public required Sounds Sounds { get; init; }
    public Action OpenSettings { get; set; } = () => { };

    /// Opens a web address or file the way Windows would. Anything else is ignored.
    public static void Open(string target)
    {
        try
        {
            var web = Uri.TryCreate(target, UriKind.Absolute, out var u) && u.Scheme is "http" or "https";
            if (web || File.Exists(target) || Directory.Exists(target)) Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception e) { Program.Log(e); }
    }
}

/// Small soft tones, made here so there are no sound files to ship.
sealed class Sounds(Settings settings)
{
    public enum Tone { NeedsYou, Finished, Failed, Timer }
    const int Rate = 44100;
    DateTime _last;
    public Func<bool> Silent { get; set; } = () => false;

    public void Play(Tone t, bool preview = false)
    {
        // Test copies never make a noise.
        if (Program.Name != "Noch" && !preview) return;
        if (!preview && (!settings.Sounds || Silent())) return;
        var now = DateTime.UtcNow;
        if (!preview && now - _last < TimeSpan.FromMilliseconds(700)) return;
        _last = now;
        var volume = settings.SoundVolume;
        if (volume <= 0) return;
        Task.Run(() =>
        {
            try { using var ms = new MemoryStream(Wave(t, volume)); using var p = new System.Media.SoundPlayer(ms); p.PlaySync(); }
            catch { }
        });
    }

    static (double Hz, double At)[] Notes(Tone t) => t switch
    {
        Tone.NeedsYou => [(659.26, 0), (880, .13)],
        Tone.Finished => [(523.25, 0), (783.99, .09), (1046.5, .18)],
        Tone.Failed => [(392, 0), (311.13, .16)],
        _ => [(783.99, 0), (783.99, .22), (1046.5, .44)]
    };

    /// A plucked, marimba-like tone: a sine, a faint overtone, and a quick fade.
    public static byte[] Wave(Tone t, double volume)
    {
        var notes = Notes(t);
        var n = (int)((notes[^1].At + .55) * Rate);
        var mix = new double[n];
        foreach (var (hz, at) in notes)
            for (var i = (int)(at * Rate); i < n; i++)
            {
                var s = (double)i / Rate - at;
                var attack = Math.Min(1, s / .004);
                mix[i] += attack * (Math.Sin(2 * Math.PI * hz * s) * Math.Exp(-7.5 * s) + .22 * Math.Sin(2 * Math.PI * hz * 4 * s) * Math.Exp(-26 * s));
            }
        // Fade the very end so it never clicks.
        for (var i = 0; i < 400; i++) mix[n - 1 - i] *= i / 400.0;
        var peak = Math.Max(1e-6, mix.Max(Math.Abs));
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(36 + n * 2); w.Write("WAVEfmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(n * 2);
        foreach (var v in mix) w.Write((short)(v / peak * Math.Clamp(volume, 0, 1) * .5 * short.MaxValue));
        return ms.ToArray();
    }
}

/// Speaker volume, changed by scrolling over the music.
static class Volume
{
    static IAudioEndpointVolume Speakers()
    {
        var e = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        e.GetDefaultAudioEndpoint(0, 1, out var device);
        var iid = typeof(IAudioEndpointVolume).GUID;
        device.Activate(ref iid, 1, IntPtr.Zero, out var o);
        return (IAudioEndpointVolume)o;
    }

    public static float Get() { Speakers().GetMasterVolumeLevelScalar(out var now); return now; }
    public static void Set(float to) { var none = Guid.Empty; Speakers().SetMasterVolumeLevelScalar(to, ref none); }

    /// In steps of two, like the volume keys. Null when there is no sound device.
    public static double? Change(double by)
    {
        try
        {
            var to = (float)Math.Clamp(Math.Round((Get() + by) * 50) / 50, 0, 1);
            Set(to);
            return to;
        }
        catch { return null; }
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator { void EnumAudioEndpoints(int flow, int state, out IntPtr devices); void GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device); }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice { void Activate(ref Guid iid, int ctx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object o); }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        void RegisterControlChangeNotify(IntPtr p);
        void UnregisterControlChangeNotify(IntPtr p);
        void GetChannelCount(out uint n);
        void SetMasterVolumeLevel(float db, ref Guid ctx);
        void SetMasterVolumeLevelScalar(float level, ref Guid ctx);
        void GetMasterVolumeLevel(out float db);
        void GetMasterVolumeLevelScalar(out float level);
    }
}

/// Plugging in, and running low.
sealed class PowerWatch
{
    readonly Notices _notices;
    readonly Func<bool> _on;
    bool? _plugged;
    int _warned = 101;

    public PowerWatch(Notices notices, Func<bool> on)
    {
        _notices = notices; _on = on;
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        t.Tick += (_, _) => { var p = Forms.SystemInformation.PowerStatus; Look(p.PowerLineStatus == Forms.PowerLineStatus.Online, p.BatteryChargeStatus.HasFlag(Forms.BatteryChargeStatus.NoSystemBattery) ? -1 : (int)Math.Round(p.BatteryLifePercent * 100)); };
        t.Start();
    }

    internal void Look(bool plugged, int percent)
    {
        if (percent is < 0 or > 100) return;
        var first = _plugged is null;
        if (!first && _plugged != plugged && _on())
            _notices.Post(new Notice("power", NoticeKind.Power, plugged ? "Charging" : "On battery", $"{percent}%") { Level = percent / 100.0, Life = TimeSpan.FromSeconds(3) });
        if (plugged) _warned = 101;
        else
            foreach (var step in new[] { 20, 10, 5 })
                if (percent <= step && _warned > step)
                {
                    _warned = step;
                    if (_on()) _notices.Post(new Notice("power", NoticeKind.Warning, "Battery low", $"{percent}% left") { Level = percent / 100.0, Life = TimeSpan.FromSeconds(6) });
                    break;
                }
        _plugged = plugged;
    }
}

/// Keeps an eye on one calendar feed.
sealed class CalendarService
{
    const int MaxBytes = 12 * 1024 * 1024;
    static readonly HttpClient Http = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(10) }) { Timeout = TimeSpan.FromSeconds(25), MaxResponseContentBufferSize = MaxBytes };
    readonly Settings _settings;
    readonly Notices _notices;
    readonly Sounds _sounds;
    readonly DispatcherTimer _fetch = new() { Interval = TimeSpan.FromMinutes(5) }, _tick = new() { Interval = TimeSpan.FromSeconds(5) };
    readonly HashSet<string> _told = [];
    int _run;
    public IReadOnlyList<Meeting> Meetings { get; internal set; } = [];
    public string Status { get; private set; } = "";
    public event Action? Changed;

    public CalendarService(Settings settings, Notices notices, Sounds sounds)
    {
        _settings = settings; _notices = notices; _sounds = sounds;
        _fetch.Tick += (_, _) => Fetch();
        _tick.Tick += (_, _) => Remind(DateTime.UtcNow);
    }

    public bool On => !string.IsNullOrEmpty(_settings.CalendarSecret);

    public void Start()
    {
        _fetch.Stop(); _tick.Stop();
        if (!On) { _run++; Meetings = []; Status = ""; Changed?.Invoke(); return; }
        _fetch.Start(); _tick.Start();
        Fetch();
    }

    /// Returns a message if the link is no good.
    public string? SetLink(string pasted)
    {
        if (Agenda.Feed(pasted) is not { } u) return "That is not a calendar link. It should start with https:// or webcal://";
        _settings.CalendarSecret = Protect(u.AbsoluteUri);
        _settings.Save();
        Status = "Checking…";
        Start();
        return null;
    }

    public void Forget() { _settings.CalendarSecret = null; _settings.Save(); Start(); }

    // The link works like a password, so it is stored encrypted for this Windows user.
    static string Protect(string s) => Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(s), null, DataProtectionScope.CurrentUser));
    static string? Reveal(string? s) { try { return s is null ? null : Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(s), null, DataProtectionScope.CurrentUser)); } catch { return null; } }

    async void Fetch()
    {
        var run = ++_run;
        try
        {
            if (Agenda.Feed(Reveal(_settings.CalendarSecret)) is not { } url) { Status = "The saved link could not be read. Paste it again."; return; }
            var text = await Http.GetStringAsync(url);
            var now = DateTime.UtcNow;
            var list = await Task.Run(() => Agenda.Parse(text, now.AddHours(-1), now.AddHours(36)));
            if (run != _run) return;
            Meetings = list;
            Status = list.Count == 0 ? "Connected. Nothing in the next day." : $"Connected. {list.Count} in the next day and a half.";
        }
        catch (Exception e)
        {
            if (run != _run) return;
            // Keep the meetings already known; a dropped connection should not hide the next one.
            Status = e is HttpRequestException or TaskCanceledException ? "Could not reach the calendar. Trying again soon." : "That address did not return a calendar.";
        }
        finally { if (run == _run) Changed?.Invoke(); }
    }

    public Meeting? Next(DateTime now) => Meetings.FirstOrDefault(m => m.End > now && m.Start > now.AddMinutes(-10));
    /// Close enough to belong on the bar.
    public Meeting? Soon(DateTime now) => Next(now) is { } m && m.Start - now < TimeSpan.FromMinutes(10) && now - m.Start < TimeSpan.FromMinutes(3) ? m : null;

    internal void Remind(DateTime now)
    {
        foreach (var m in Meetings)
        {
            var left = m.Start - now;
            if (left > TimeSpan.FromSeconds(75) || left < TimeSpan.FromMinutes(-2) || !_told.Add(m.Id)) continue;
            _notices.Post(new Notice("meeting|" + m.Id, NoticeKind.Meeting, m.Title, left > TimeSpan.FromSeconds(15) ? "Starts in a minute" : "Starting now")
            {
                ActionLabel = m.Link is null ? null : "Join", Action = m.Link is null ? null : () => AppServices.Open(m.Link), Life = TimeSpan.FromSeconds(45)
            });
            _sounds.Play(Sounds.Tone.NeedsYou);
        }
        Changed?.Invoke();
    }
}

/// Shows what other apps announce. Off unless turned on, and nothing is kept.
sealed class NotificationService
{
    readonly Settings _settings;
    readonly Notices _notices;
    readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(1) };
    HashSet<uint>? _seen;
    bool _busy;
    public string Status { get; private set; } = "";
    /// Apps that have sent something since start, for the settings list.
    public SortedSet<string> Apps { get; } = new(StringComparer.CurrentCultureIgnoreCase);

    public NotificationService(Settings settings, Notices notices)
    {
        _settings = settings; _notices = notices;
        // Windows only raises its change event for Store-packaged apps, so look once a second.
        _poll.Tick += async (_, _) => await Look();
    }

    public async Task Start()
    {
        _poll.Stop(); _seen = null;
        if (!_settings.Notifications) { Status = ""; _notices.DismissKind(NoticeKind.App); return; }
        try
        {
            var access = await UserNotificationListener.Current.RequestAccessAsync();
            if (access != UserNotificationListenerAccessStatus.Allowed) { Status = "Windows is blocking this. Allow it in Settings > Privacy > Notifications."; return; }
            Status = "";
            _poll.Start();
        }
        catch (Exception e) { Status = "Not available on this version of Windows."; Program.Log(e); }
    }

    async Task Look()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var list = await UserNotificationListener.Current.GetNotificationsAsync(NotificationKinds.Toast);
            // What was already there when this was turned on is old news.
            if (_seen is null) { _seen = [.. list.Select(n => n.Id)]; return; }
            foreach (var n in list.Where(n => !_seen.Contains(n.Id)).OrderBy(n => n.CreationTime).TakeLast(3)) await Post(n);
            _seen = [.. list.Select(n => n.Id)];
        }
        catch (Exception e) when (e is COMException or UnauthorizedAccessException or InvalidOperationException) { _seen = null; }
        finally { _busy = false; }
    }

    async Task Post(UserNotification n)
    {
        string app = "", id = "";
        BitmapSource? icon = null;
        try
        {
            app = n.AppInfo.DisplayInfo.DisplayName; id = n.AppInfo.AppUserModelId;
            var logo = n.AppInfo.DisplayInfo.GetLogo(new Windows.Foundation.Size(64, 64));
            if (logo != null)
            {
                using var s = await logo.OpenReadAsync();
                using var ms = new MemoryStream();
                await s.AsStreamForRead().CopyToAsync(ms);
                ms.Position = 0;
                var b = new BitmapImage();
                b.BeginInit(); b.CacheOption = BitmapCacheOption.OnLoad; b.DecodePixelHeight = 64; b.StreamSource = ms; b.EndInit(); b.Freeze();
                icon = b;
            }
        }
        catch { /* some apps have no picture */ }
        if (app.Length == 0) app = "Notification";
        Apps.Add(app);
        if (app == "Noch" || _settings.MutedApps.Contains(app)) return;
        string title = app, text = "";
        try
        {
            var lines = n.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric)?.GetTextElements().Select(t => t.Text.Trim()).Where(t => t.Length > 0).ToList() ?? [];
            if (!_settings.HideNotificationText && lines.Count > 0) { title = Describe.OneLine(lines[0], 80); text = Describe.OneLine(string.Join(" ", lines.Skip(1)), 180); }
        }
        catch { }
        _notices.Post(new Notice("app|" + n.Id, NoticeKind.App, title, title == app ? "New notification" : app)
        {
            Body = text, Icon = icon, ActionLabel = id.Length > 0 ? "Open" : null, Action = () => Launch(id),
            SideLabel = "Mute", Side = () => { _settings.MutedApps.Add(app); _settings.Save(); }
        });
    }

    static void Launch(string aumid)
    {
        try
        {
            var p = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            p.ArgumentList.Add(@"shell:AppsFolder\" + aumid);
            Process.Start(p)?.Dispose();
        }
        catch (Exception e) { Program.Log(e); }
    }
}

/// Pictures of files, the same ones Explorer shows.
static class FileIcon
{
    public static BitmapSource? For(string path, int size)
    {
        IntPtr bmp = IntPtr.Zero;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var f);
            f.GetImage(new SIZE { cx = size, cy = size }, 0, out bmp);
            var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(bmp, IntPtr.Zero, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            // The handle carries transparency that the line above ignores.
            var px = new byte[src.PixelWidth * src.PixelHeight * 4];
            GetBitmapBits(bmp, px.Length, px);
            var any = false;
            for (var i = 3; i < px.Length; i += 4) if (px[i] != 0) { any = true; break; }
            var fmt = any ? System.Windows.Media.PixelFormats.Pbgra32 : System.Windows.Media.PixelFormats.Bgr32;
            var b = BitmapSource.Create(src.PixelWidth, src.PixelHeight, 96, 96, fmt, null, px, src.PixelWidth * 4);
            b.Freeze();
            return b;
        }
        catch { return null; }
        finally { if (bmp != IntPtr.Zero) DeleteObject(bmp); }
    }

    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
    [DllImport("shell32", CharSet = CharSet.Unicode, PreserveSig = false)] static extern void SHCreateItemFromParsingName(string path, IntPtr ctx, ref Guid iid, out IShellItemImageFactory o);
    [DllImport("gdi32")] static extern bool DeleteObject(IntPtr h);
    [DllImport("gdi32")] static extern int GetBitmapBits(IntPtr h, int n, byte[] b);

    [ComImport, Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory { void GetImage(SIZE size, int flags, out IntPtr bmp); }
}
