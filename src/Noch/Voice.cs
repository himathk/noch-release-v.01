using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Noch.Core;
using Whisper.net;

namespace Noch;

/// Hold Ctrl and Alt, talk, let go: what was said is written down on this PC.
sealed class VoiceService
{
    public enum Phase { Off, Listening, Working, Said, Asking, Answer, Note, NeedsModel, Downloading }

    const string ModelName = "ggml-base.en.bin";
    const string ModelUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/" + ModelName;
    public const int ModelMegabytes = 142;
    static readonly TimeSpan Longest = TimeSpan.FromMinutes(5);

    readonly Settings _settings;
    readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    readonly DispatcherTimer _keys = new() { Interval = TimeSpan.FromMilliseconds(40) }, _away = new();
    readonly HoldKeys _hold = new();
    IMic? _mic;
    Assistant? _who;
    Task<WhisperFactory>? _whisper;
    CancellationTokenSource? _work;

    public Phase Now { get; private set; }
    public string Text { get; private set; } = "";
    /// What was asked, while it is being answered and after.
    public string Question { get; private set; } = "";
    /// Who is answering: Claude or Codex.
    public string By { get; private set; } = "";
    public DateTime Started { get; private set; }
    public double Progress { get; private set; }
    /// How loud it is right now, 0 to 1.
    public double Level => _mic?.Level ?? 0;
    public bool Showing => Now != Phase.Off;
    public event Action? Changed;

    public static string ModelPath => Path.Combine(Settings.Folder, "models", ModelName);

    public VoiceService(Settings settings)
    {
        _settings = settings;
        _keys.Tick += (_, _) => WatchKeys();
        _away.Tick += (_, _) => { _away.Stop(); if (Now is Phase.Said or Phase.Answer or Phase.Note or Phase.NeedsModel) Set(Phase.Off); };
        _keys.Start();
    }

    void Set(Phase p, string text = "", TimeSpan? stay = null)
    {
        Now = p; Text = text;
        _away.Stop();
        if (stay is { } t) { _away.Interval = t; _away.Start(); }
        Changed?.Invoke();
    }

    // ---- the keys ----

    static bool Down(int vk) => (Native.GetAsyncKeyState(vk) & 0x8000) != 0;

    /// Anything held besides Ctrl, Alt and Shift: a shortcut is being typed, not a question asked.
    static bool OtherKey()
    {
        for (var vk = 1; vk < 0xFF; vk++)
        {
            if (vk is 0x10 or 0x11 or 0x12 || vk is >= 0xA0 and <= 0xA5) continue;
            if (Down(vk)) return true;
        }
        return false;
    }

    void WatchKeys()
    {
        bool ctrl = Down(0x11), alt = Down(0x12), listening = Now == Phase.Listening && _mic is Microphone;
        // Only looked at while both are down, since it asks about every key.
        var other = ctrl && alt && OtherKey();
        var now = DateTime.UtcNow;
        switch (_hold.Step(ctrl, alt, other, listening, now))
        {
            case KeyAction.Start when _settings.Voice && Now is not (Phase.Working or Phase.Downloading): Begin(() => new Microphone()); break;
            case KeyAction.Finish: Finish(); break;
            case KeyAction.Cancel: Cancel(); break;
            default: if (listening && now - Started > Longest) Finish(); break;
        }
    }

    // ---- listening ----

    void Begin(Func<IMic> open)
    {
        if (!File.Exists(ModelPath)) { Set(Phase.NeedsModel, stay: TimeSpan.FromSeconds(12)); return; }
        _work?.Cancel();
        try { _mic = open(); }
        catch (Exception e) { Program.Log(e); Set(Phase.Note, "No microphone to listen with", TimeSpan.FromSeconds(3)); return; }
        Started = DateTime.UtcNow;
        // Claude Code takes a few seconds to start, so it starts while the question is still being asked.
        _who?.Cool();
        _who = Assistant.Pick(_settings);
        _who?.Warm();
        _ = Load().ContinueWith(t => _whisper = null, TaskContinuationOptions.OnlyOnFaulted);
        Set(Phase.Listening);
    }

    void Cancel()
    {
        _mic?.Stop(); _mic = null;
        _who?.Cool();
        Set(Phase.Off);
    }

    async void Finish()
    {
        var who = _who;
        try { await Hear(who); }
        // Whatever happened, a copy that was started and never asked is not left running.
        finally { who?.Cool(); }
    }

    async Task Hear(Assistant? who)
    {
        var mic = _mic; _mic = null;
        if (mic is null) { Set(Phase.Off); return; }
        var samples = mic.Stop();
        // A tap, not a question.
        if (samples.Length < Microphone.Rate * .4) { Set(Phase.Off); return; }
        if (mic.Loudest < .01) { Set(Phase.Note, "Didn't hear anything. Is the microphone on?", TimeSpan.FromSeconds(4)); return; }
        Set(Phase.Working);
        var work = _work = new CancellationTokenSource();
        try
        {
            var text = await Task.Run(() => Transcribe(samples, work.Token));
            if (work.IsCancellationRequested) return;
            if (text.Length == 0) { Set(Phase.Note, "Didn't catch that", TimeSpan.FromSeconds(3)); return; }
            // Without Claude Code or Codex there is no one to ask, so it shows what it heard.
            if (who is null) { Set(Phase.Said, text, TimeSpan.FromSeconds(Math.Clamp(3 + text.Length / 15.0, 5, 20))); return; }
            Question = text; By = who.Name;
            Set(Phase.Asking, text);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(work.Token);
            limit.CancelAfter(TimeSpan.FromMinutes(2));
            string answer;
            // The answer shows as it is written, rather than all at once at the end.
            void Partial(string so) => _ui.BeginInvoke(() => { if (!work.IsCancellationRequested && Now is Phase.Asking or Phase.Answer) Set(Phase.Answer, so); });
            try { answer = await who.Ask(text, Partial, limit.Token); }
            catch (OperationCanceledException) when (!work.IsCancellationRequested) { throw new AskProblem($"{who.Name} took too long to answer"); }
            if (work.IsCancellationRequested) return;
            // Let the last streamed piece land first, so it cannot overwrite the whole answer.
            await _ui.InvokeAsync(() => { }, DispatcherPriority.Background);
            if (work.IsCancellationRequested) return;
            Set(Phase.Answer, answer, TimeSpan.FromSeconds(Math.Clamp(8 + answer.Length / 12.0, 12, 90)));
        }
        catch (AskProblem p) when (!work.IsCancellationRequested) { Set(Phase.Note, p.Message, TimeSpan.FromSeconds(8)); }
        catch (Exception e) when (!work.IsCancellationRequested)
        {
            Program.Log(e);
            Set(Phase.Note, Now == Phase.Asking ? $"Couldn't reach {By}" : "Couldn't write that down", TimeSpan.FromSeconds(4));
        }
    }

    async Task<string> Transcribe(float[] samples, CancellationToken stop)
    {
        WhisperFactory model;
        try { model = await Load(); }
        catch { _whisper = null; throw; }
        using var p = model.CreateBuilder().WithLanguage("en").WithThreads(Math.Clamp(Environment.ProcessorCount / 2, 2, 8)).Build();
        var parts = new List<string>();
        await foreach (var s in p.ProcessAsync(samples, stop)) parts.Add(s.Text);
        return Clean(string.Concat(parts));
    }

    /// Read from disk once, while the first question is still being asked.
    Task<WhisperFactory> Load() => _whisper ??= Task.Run(() => WhisperFactory.FromPath(ModelPath));

    /// Whisper names silence and noises in brackets, like [BLANK_AUDIO] or (wind blowing).
    internal static string Clean(string s)
    {
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\[[^\]]*\]|\([^)]*\)|\*[^*]*\*", " ");
        return System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
    }

#if DEBUG
    /// Plays a recording in place of the microphone, for testing without anyone talking.
    public void DemoTalk(string wav)
    {
        Begin(() => new FileMic(wav));
        if (_mic is FileMic f) { var t = new DispatcherTimer { Interval = f.Length + TimeSpan.FromMilliseconds(300) }; t.Tick += (_, _) => { t.Stop(); Finish(); }; t.Start(); }
    }
#endif

    public void Dismiss()
    {
        if (Now is Phase.Listening or Phase.Working or Phase.Downloading) return;
        // Waved away while waiting: the answer is no longer wanted.
        if (Now == Phase.Asking) _work?.Cancel();
        Set(Phase.Off);
    }

    // ---- the speech file, fetched once ----

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(20) };

    public async void Download()
    {
        if (Now == Phase.Downloading) return;
        Progress = 0;
        Set(Phase.Downloading);
        var part = ModelPath + ".part";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ModelPath)!);
            using (var r = await Http.GetAsync(ModelUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                r.EnsureSuccessStatusCode();
                var total = r.Content.Headers.ContentLength ?? ModelMegabytes * 1_000_000L;
                await using var from = await r.Content.ReadAsStreamAsync();
                await using var to = File.Create(part);
                var buffer = new byte[1 << 16];
                long got = 0; var shown = DateTime.MinValue;
                for (int n; (n = await from.ReadAsync(buffer)) > 0;)
                {
                    await to.WriteAsync(buffer.AsMemory(0, n));
                    got += n;
                    if (DateTime.UtcNow - shown > TimeSpan.FromMilliseconds(250)) { shown = DateTime.UtcNow; Progress = Math.Min(1, got / (double)total); Changed?.Invoke(); }
                }
                if (got < 10_000_000) throw new IOException("The speech file came back too small.");
            }
            File.Move(part, ModelPath, true);
            Set(Phase.Note, "Ready. Hold Ctrl Alt and talk", TimeSpan.FromSeconds(4));
        }
        catch (Exception e)
        {
            Program.Log(e);
            try { File.Delete(part); } catch { }
            Set(Phase.Note, "Couldn't get the speech file. Try again later", TimeSpan.FromSeconds(4));
        }
    }
}

interface IMic
{
    double Level { get; }
    double Loudest { get; }
    float[] Stop();
}

#if DEBUG
/// A 16 kHz mono recording played out in real time, as if someone were talking.
sealed class FileMic : IMic
{
    readonly float[] _all;
    readonly DateTime _start = DateTime.UtcNow;
    public FileMic(string wav)
    {
        var b = File.ReadAllBytes(wav);
        var at = 12;
        while (at + 8 <= b.Length && System.Text.Encoding.ASCII.GetString(b, at, 4) != "data") at += 8 + BitConverter.ToInt32(b, at + 4);
        var n = BitConverter.ToInt32(b, at + 4) / 2;
        _all = new float[n];
        for (var i = 0; i < n; i++) _all[i] = BitConverter.ToInt16(b, at + 8 + i * 2) / 32768f;
        Loudest = _all.Length == 0 ? 0 : _all.Max(Math.Abs);
    }
    public TimeSpan Length => TimeSpan.FromSeconds(_all.Length / (double)Microphone.Rate);
    int Heard => Math.Min(_all.Length, (int)((DateTime.UtcNow - _start).TotalSeconds * Microphone.Rate));
    public double Level
    {
        get
        {
            int end = Heard, from = Math.Max(0, end - Microphone.Rate / 10);
            if (end <= from) return 0;
            double sum = 0; for (var i = from; i < end; i++) sum += _all[i] * _all[i];
            return Microphone.ToLevel(Math.Sqrt(sum / (end - from)));
        }
    }
    public double Loudest { get; }
    public float[] Stop() => _all[..Heard];
}
#endif

/// The default microphone, 16 kHz mono, which is what speech recognition wants. Records only while it exists.
sealed class Microphone : IMic
{
    public const int Rate = 16000;
    const int Buffers = 4, BufferBytes = Rate / 10 * 2; // a tenth of a second each

    readonly IntPtr _h;
    readonly IntPtr[] _headers = new IntPtr[Buffers];
    readonly List<float> _samples = [];
    readonly Thread _thread;
    volatile bool _stop;
    double _level, _loudest;

    public double Level => Volatile.Read(ref _level);
    public double Loudest => _loudest;

    public Microphone()
    {
        var f = new WAVEFORMATEX { wFormatTag = 1, nChannels = 1, nSamplesPerSec = Rate, nAvgBytesPerSec = Rate * 2, nBlockAlign = 2, wBitsPerSample = 16 };
        var err = waveInOpen(out _h, -1, ref f, IntPtr.Zero, IntPtr.Zero, 0);
        if (err != 0) throw new IOException("The microphone could not be opened (" + err + ").");
        var size = Marshal.SizeOf<WAVEHDR>();
        for (var i = 0; i < Buffers; i++)
        {
            var hdr = new WAVEHDR { lpData = Marshal.AllocHGlobal(BufferBytes), dwBufferLength = BufferBytes };
            _headers[i] = Marshal.AllocHGlobal(size);
            Marshal.StructureToPtr(hdr, _headers[i], false);
            waveInPrepareHeader(_h, _headers[i], size);
            waveInAddBuffer(_h, _headers[i], size);
        }
        waveInStart(_h);
        _thread = new Thread(Run) { IsBackground = true, Name = "Microphone" };
        _thread.Start();
    }

    static readonly int FlagsAt = (int)Marshal.OffsetOf<WAVEHDR>(nameof(WAVEHDR.dwFlags));

    void Run()
    {
        var size = Marshal.SizeOf<WAVEHDR>();
        while (!_stop)
        {
            foreach (var h in _headers)
            {
                if ((Marshal.ReadInt32(h, FlagsAt) & 1) == 0) continue;
                Take(h);
                Marshal.WriteInt32(h, FlagsAt, Marshal.ReadInt32(h, FlagsAt) & ~1);
                waveInAddBuffer(_h, h, size);
            }
            Thread.Sleep(15);
        }
    }

    void Take(IntPtr h)
    {
        var hdr = Marshal.PtrToStructure<WAVEHDR>(h);
        var n = hdr.dwBytesRecorded / 2;
        if (n == 0) return;
        var raw = new short[n];
        Marshal.Copy(hdr.lpData, raw, 0, n);
        double sum = 0;
        lock (_samples)
            foreach (var v in raw) { var x = v / 32768f; _samples.Add(x); sum += x * x; }
        var rms = Math.Sqrt(sum / n);
        _loudest = Math.Max(_loudest, rms);
        Volatile.Write(ref _level, ToLevel(rms));
    }

    /// Speech sits between about -50 and -10 dB.
    public static double ToLevel(double rms) => Math.Clamp((20 * Math.Log10(Math.Max(rms, 1e-6)) + 52) / 40, 0, 1);

    /// Stops recording and hands back everything it heard.
    public float[] Stop()
    {
        if (_stop) lock (_samples) return [.. _samples];
        _stop = true;
        _thread.Join(500);
        waveInReset(_h);
        var size = Marshal.SizeOf<WAVEHDR>();
        foreach (var h in _headers)
        {
            if ((Marshal.ReadInt32(h, FlagsAt) & 1) != 0) Take(h);
            waveInUnprepareHeader(_h, h, size);
            Marshal.FreeHGlobal(Marshal.PtrToStructure<WAVEHDR>(h).lpData);
            Marshal.FreeHGlobal(h);
        }
        waveInClose(_h);
        Volatile.Write(ref _level, 0);
        lock (_samples) return [.. _samples];
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WAVEFORMATEX { public short wFormatTag, nChannels; public int nSamplesPerSec, nAvgBytesPerSec; public short nBlockAlign, wBitsPerSample, cbSize; }

    [StructLayout(LayoutKind.Sequential)]
    struct WAVEHDR { public IntPtr lpData; public int dwBufferLength, dwBytesRecorded; public IntPtr dwUser; public int dwFlags, dwLoops; public IntPtr lpNext, reserved; }

    [DllImport("winmm")] static extern int waveInOpen(out IntPtr h, int device, ref WAVEFORMATEX format, IntPtr callback, IntPtr instance, int flags);
    [DllImport("winmm")] static extern int waveInPrepareHeader(IntPtr h, IntPtr hdr, int size);
    [DllImport("winmm")] static extern int waveInUnprepareHeader(IntPtr h, IntPtr hdr, int size);
    [DllImport("winmm")] static extern int waveInAddBuffer(IntPtr h, IntPtr hdr, int size);
    [DllImport("winmm")] static extern int waveInStart(IntPtr h);
    [DllImport("winmm")] static extern int waveInReset(IntPtr h);
    [DllImport("winmm")] static extern int waveInClose(IntPtr h);
}
