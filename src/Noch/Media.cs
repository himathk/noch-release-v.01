using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;
using Session = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;
using Manager = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager;
using Status = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus;

namespace Noch;

sealed record NowPlaying
{
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string Source { get; init; } = "";
    public bool Playing { get; init; }
    public TimeSpan Position { get; init; }
    public TimeSpan Duration { get; init; }
    public DateTimeOffset At { get; init; }
    public double Rate { get; init; } = 1;
    public bool CanNext { get; init; }
    public bool CanPrevious { get; init; }
    public bool CanSeek { get; init; }
    public BitmapSource? Art { get; init; }
    public Color Tint { get; init; } = Theme.MediaColor;

    public TimeSpan Now()
    {
        var p = Playing ? Position + (DateTimeOffset.Now - At) * Rate : Position;
        return p < TimeSpan.Zero ? TimeSpan.Zero : Duration > TimeSpan.Zero && p > Duration ? Duration : p;
    }
}

/// Reads whatever Windows says is playing: Spotify, YouTube in a browser, or anything else.
sealed class MediaService
{
    readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    readonly DispatcherTimer _soon;
    readonly List<Session> _watched = [];
    Manager? _manager;
    Session? _session;
    bool _busy, _again;
    (string Key, BitmapSource? Art, Color Tint) _art;
    (string Key, string Source) _site;

    public NowPlaying? Current { get; private set; }
    public DateTime LastPlaying { get; private set; } = DateTime.MinValue;
    public event Action? Changed;

    public MediaService()
    {
        _soon = new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Background, async (_, _) => { _soon!.Stop(); await Refresh(); }, _ui);
        _soon.Stop();
    }

    public async void Start()
    {
        try
        {
            _manager = await Manager.RequestAsync();
            _manager.SessionsChanged += (_, _) => Queue(true);
            _manager.CurrentSessionChanged += (_, _) => Queue(false);
            Watch();
            await Refresh();
        }
        catch { /* media info is optional */ }
    }

    void Queue(bool rewatch) => _ui.BeginInvoke(() => { if (rewatch) Watch(); _soon.Stop(); _soon.Start(); });
    void OnPlayback(Session s, PlaybackInfoChangedEventArgs e) => Queue(false);
    void OnProps(Session s, MediaPropertiesChangedEventArgs e) => _ui.BeginInvoke(() => { _art = default; Queue(false); });
    void OnTimeline(Session s, TimelinePropertiesChangedEventArgs e) => Queue(false);

    void Watch()
    {
        foreach (var s in _watched)
            try { s.PlaybackInfoChanged -= OnPlayback; s.MediaPropertiesChanged -= OnProps; s.TimelinePropertiesChanged -= OnTimeline; } catch { }
        _watched.Clear();
        try
        {
            foreach (var s in _manager!.GetSessions())
            {
                s.PlaybackInfoChanged += OnPlayback; s.MediaPropertiesChanged += OnProps; s.TimelinePropertiesChanged += OnTimeline;
                _watched.Add(s);
            }
        }
        catch { }
    }

    static bool IsPlaying(Session s) { try { return s.GetPlaybackInfo().PlaybackStatus == Status.Playing; } catch { return false; } }

    async Task Refresh()
    {
        if (_manager is null) return;
        if (_busy) { _again = true; return; }
        _busy = true;
        try
        {
            NowPlaying? next = null;
            try { next = await Read(); } catch { }
            if (next?.Playing == true || Current?.Playing == true) LastPlaying = DateTime.UtcNow;
            Current = next;
            Changed?.Invoke();
        }
        finally { _busy = false; }
        if (_again) { _again = false; await Refresh(); }
    }

    async Task<NowPlaying?> Read()
    {
        var system = _manager!.GetCurrentSession();
        var playing = _watched.Where(IsPlaying).ToList();
        // Whatever is audible wins; stay on the same app while it keeps playing.
        _session = playing.FirstOrDefault(s => s.SourceAppUserModelId == _session?.SourceAppUserModelId)
            ?? playing.FirstOrDefault(s => s.SourceAppUserModelId == system?.SourceAppUserModelId)
            ?? playing.FirstOrDefault() ?? system;
        if (_session is null) return null;

        var props = await _session.TryGetMediaPropertiesAsync();
        if (props is null || string.IsNullOrWhiteSpace(props.Title)) return null;
        var info = _session.GetPlaybackInfo();
        var time = _session.GetTimelineProperties();
        var key = _session.SourceAppUserModelId + "|" + props.Title + "|" + props.Artist;
        if (_art.Key != key || _art.Art is null) _art = await LoadArt(key, props);

        return new NowPlaying
        {
            Title = props.Title,
            Artist = props.Artist is { Length: > 0 } a ? a : props.AlbumArtist ?? "",
            Source = SourceName(_session.SourceAppUserModelId, props.Title),
            Playing = info.PlaybackStatus == Status.Playing,
            Position = time.Position,
            Duration = time.EndTime - time.StartTime,
            // Some apps never fill this in, which would make the position jump by centuries.
            At = time.LastUpdatedTime.Year > 2000 ? time.LastUpdatedTime : DateTimeOffset.Now,
            Rate = info.PlaybackRate ?? 1,
            CanNext = info.Controls.IsNextEnabled,
            CanPrevious = info.Controls.IsPreviousEnabled,
            CanSeek = info.Controls.IsPlaybackPositionEnabled,
            Art = _art.Art,
            Tint = _art.Art is null ? Theme.MediaColor : _art.Tint
        };
    }

    string SourceName(string id, string title)
    {
        var l = id.ToLowerInvariant();
        var browser = l.Contains("chrome") ? "Chrome" : l.Contains("msedge") ? "Edge" : l.Contains("firefox") || l.Contains("308046b0af4a39cb") ? "Firefox"
            : l.Contains("brave") ? "Brave" : l.Contains("opera") ? "Opera" : l.Contains("vivaldi") ? "Vivaldi" : l.Contains("arc") && l.Contains("browser") ? "Arc" : null;
        if (l.Contains("spotify")) return "Spotify";
        if (browser is null)
        {
            var name = id.Split('!')[^1];
            name = Path.GetFileNameWithoutExtension(name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".x").Split('_')[0];
            return name.Length > 0 ? char.ToUpperInvariant(name[0]) + name[1..] : "Media";
        }
        // Windows does not say which site it is, but the tab title does while it is in front.
        var key = id + "|" + title;
        if (_site.Key != key) _site = (key, "");
        if (_site.Source.Length == 0)
        {
            var site = Native.AnyWindowTitle(t => t.Contains(title, StringComparison.Ordinal) && t.Contains("YouTube Music", StringComparison.Ordinal)) ? "YouTube Music"
                : Native.AnyWindowTitle(t => t.Contains(title + " - YouTube", StringComparison.Ordinal)) ? "YouTube" : null;
            if (site != null) _site = (key, site + " in " + browser);
        }
        return _site.Source.Length > 0 ? _site.Source : browser;
    }

    static async Task<(string, BitmapSource?, Color)> LoadArt(string key, GlobalSystemMediaTransportControlsSessionMediaProperties props)
    {
        try
        {
            if (props.Thumbnail is null) return (key, null, Theme.MediaColor);
            using var stream = await props.Thumbnail.OpenReadAsync();
            using var ms = new MemoryStream();
            await stream.AsStreamForRead().CopyToAsync(ms);
            ms.Position = 0;
            var img = new BitmapImage();
            img.BeginInit(); img.CacheOption = BitmapCacheOption.OnLoad; img.DecodePixelHeight = 216; img.StreamSource = ms; img.EndInit(); img.Freeze();
            return (key, img, TintOf(img));
        }
        catch { return (key, null, Theme.MediaColor); }
    }

    /// The strongest colour in the cover, lightened enough to read on black.
    public static Color TintOf(BitmapSource src)
    {
        var small = new FormatConvertedBitmap(new TransformedBitmap(src, new ScaleTransform(24.0 / src.PixelWidth, 24.0 / src.PixelHeight)), PixelFormats.Bgra32, null, 0);
        int w = small.PixelWidth, h = small.PixelHeight;
        var px = new byte[w * h * 4];
        small.CopyPixels(px, w * 4, 0);
        double r = 0, g = 0, b = 0, total = 0;
        for (var i = 0; i < px.Length; i += 4)
        {
            double max = Math.Max(px[i], Math.Max(px[i + 1], px[i + 2])), min = Math.Min(px[i], Math.Min(px[i + 1], px[i + 2]));
            var weight = max < 30 ? 0 : Math.Pow((max - min) / max, 2) * max / 255;
            b += px[i] * weight; g += px[i + 1] * weight; r += px[i + 2] * weight; total += weight;
        }
        if (total < w * h * 0.02) return Color.FromRgb(0xD8, 0xDA, 0xDB);
        r /= total; g /= total; b /= total;
        var top = Math.Max(r, Math.Max(g, b));
        var k = 255 / Math.Max(top, 1);
        r *= k; g *= k; b *= k;
        for (var i = 0; i < 12 && 0.2126 * r + 0.7152 * g + 0.0722 * b < 140; i++) { r += (255 - r) * .1; g += (255 - g) * .1; b += (255 - b) * .1; }
        return Color.FromRgb((byte)r, (byte)g, (byte)b);
    }

    public async void PlayPause() { try { if (_session != null) await _session.TryTogglePlayPauseAsync(); } catch { } }
    public async void Next() { try { if (_session != null) await _session.TrySkipNextAsync(); } catch { } }
    public async void Previous() { try { if (_session != null) await _session.TrySkipPreviousAsync(); } catch { } }
    public async void Seek(TimeSpan to)
    {
        try { if (_session != null) await _session.TryChangePlaybackPositionAsync((to + _session.GetTimelineProperties().StartTime).Ticks); } catch { }
    }
}
