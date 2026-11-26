using System.IO;
using System.Threading.Channels;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Noch.Core;

namespace Noch;

/// Listening in on a meeting: what is said is written down as it goes, on this PC, and when someone asks
/// a question, Claude (or Codex) is asked for an answer with the conversation so far. Nothing is saved.
sealed class ListenIn
{
    sealed record Line(DateTime At, string Who, string Text);

    readonly Settings _settings;
    readonly VoiceService _voice;
    readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    readonly List<Line> _lines = [];
    Microphone? _mic;
    IDisposable? _pc;
    Chunker? _micChunks, _pcChunks;
    Channel<(string Who, float[] Sound)>? _heard;
    CancellationTokenSource? _run;
    Assistant? _who;
    string? _waiting;
    bool _asking;

    public bool On { get; private set; }
    public DateTime Started { get; private set; }
    /// The last thing anyone said.
    public string Latest { get; private set; } = "";
    /// How loud the meeting is right now, 0 to 1.
    public double Level => Math.Max(_micChunks?.Level ?? 0, _pcChunks?.Level ?? 0);
    public event Action? Changed;

    public ListenIn(Settings settings, VoiceService voice) { _settings = settings; _voice = voice; }

    public void Toggle() { if (On) Stop(); else Start(); }

    public void Start() => Begin(() =>
    {
        // The other people on a call come out of the speakers; the person using it, and anyone in the room, go into the microphone.
        var any = false;
        try { var m = new Microphone(keep: false); m.Heard += s => Feed("You", _micChunks!, s); _mic = m; any = true; } catch (Exception e) { Program.Log(e); }
        try { var l = new Loopback(); l.Heard += s => Feed("Them", _pcChunks!, s); _pc = l; any = true; } catch (Exception e) { Program.Log(e); }
        return any;
    });

    void Begin(Func<bool> open)
    {
        if (On) return;
        if (!File.Exists(VoiceService.ModelPath)) { _voice.NeedModel(); return; }
        _lines.Clear(); Latest = ""; _waiting = null;
        _micChunks = new(); _pcChunks = new();
        _heard = Channel.CreateUnbounded<(string, float[])>();
        _run = new();
        if (!open()) { _voice.Tell("Nothing to listen to: no microphone or speakers"); return; }
        On = true; Started = DateTime.UtcNow;
        Speech.Preload();
        _who = Assistant.Pick(_settings);
        _who?.Warm(Assistant.Meeting);
        _ = Work(_heard.Reader, _run.Token);
        _voice.Tell(_who is null ? "Listening in. Install Claude Code or Codex for answers" : "Listening in. Ctrl Alt L stops");
        Changed?.Invoke();
    }

    public void Stop()
    {
        if (!On) return;
        On = false;
        _run?.Cancel();
        _mic?.Stop(); _mic = null;
        _pc?.Dispose(); _pc = null;
        _heard?.Writer.TryComplete();
        _who?.Cool(); _who = null;
        _lines.Clear(); Latest = "";
        Changed?.Invoke();
    }

    void Feed(string who, Chunker chunks, float[] samples)
    {
        lock (chunks)
            foreach (var piece in chunks.Feed(samples)) _heard?.Writer.TryWrite((who, piece));
    }

    /// One stretch at a time, so a long meeting never has two of them fighting for the processor.
    async Task Work(ChannelReader<(string Who, float[] Sound)> heard, CancellationToken stop)
    {
        try
        {
            await foreach (var (who, sound) in heard.ReadAllAsync(stop))
            {
                string text;
                try { text = await Speech.Write(sound, stop); }
                catch (OperationCanceledException) { return; }
                catch (Exception e) { Program.Log(e); continue; }
                if (text.Length > 0) await _ui.InvokeAsync(() => Heard(who, text));
            }
        }
        catch (OperationCanceledException) { }
    }

    void Heard(string who, string text)
    {
        if (!On) return;
        var now = DateTime.UtcNow;
        // Through speakers, the microphone hears the call a second time.
        if (who == "You" && _lines.Any(l => l.Who == "Them" && now - l.At < TimeSpan.FromSeconds(20) && Same(l.Text, text))) return;
        _lines.Add(new(now, who, text));
        if (_lines.Count > 300) _lines.RemoveRange(0, 100);
        Latest = text;
        Changed?.Invoke();
        if (Questions.In(text).LastOrDefault() is { } q) Ask(q);
    }

    static bool Same(string a, string b)
    {
        static HashSet<string> Words(string s) => [.. s.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => w.Trim('.', ',', '?', '!'))];
        var x = Words(a); var y = Words(b);
        return y.Count > 0 && y.Count(x.Contains) >= y.Count * .6;
    }

    async void Ask(string question)
    {
        // Still answering the last one: only the newest waiting question is kept.
        if (_asking) { _waiting = question; return; }
        _asking = true;
        try
        {
            for (string? q = question; q != null && On; q = _waiting, _waiting = null)
            {
                var who = _who ??= Assistant.Pick(_settings);
                if (who is null) return;
                var run = _run!.Token;
                var asked = q;
                void Partial(string so) => _ui.BeginInvoke(() => { if (On && !run.IsCancellationRequested && !Skip(so)) _voice.Offer(asked, so, who.Name, false); });
                var answer = await who.Ask(Prompt(q), Partial, run, Assistant.Meeting);
                if (On) who.Warm(Assistant.Meeting);
                await _ui.InvokeAsync(() => { }, DispatcherPriority.Background);
                if (!On || run.IsCancellationRequested) return;
                if (Skip(answer)) _voice.Withdraw(asked); else _voice.Offer(asked, answer, who.Name, true);
            }
        }
        catch (OperationCanceledException) { }
        catch (AskProblem p) { _voice.Tell(p.Message); }
        catch (Exception e) { Program.Log(e); }
        finally { _asking = false; }
    }

    /// Its way of saying the question needs no help. Also true for the first letters of it, while they stream in.
    static bool Skip(string s)
    {
        var t = s.Trim().ToUpperInvariant();
        return t.Length < 6 && "SKIP".StartsWith(t.TrimEnd('.')) || t.StartsWith("SKIP");
    }

    string Prompt(string question)
    {
        var said = string.Join("\n", _lines.TakeLast(40).Select(l => $"[{l.Who}] {l.Text}"));
        return "The conversation so far, most recent last:\n" + said + "\n\nJust asked: \"" + question + "\"\n\n"
            + "If this is a real question the user would want help answering, answer it now in at most three short sentences they could say out loud. "
            + "You are not in the meeting and nobody there can hear you, so never answer as a participant. "
            + "If it isn't a question you can help with (small talk, checking the call works, rhetorical, about someone's own plans or work, something only the people there could know, or already answered), reply with exactly SKIP.";
    }

#if DEBUG
    /// A recording played in as if it were the other people on a call.
    public void DemoStart(string wav) => Begin(() =>
    {
        var all = FileMic.Read(wav);
        var t = new Thread(() =>
        {
            for (var at = 0; at < all.Length && _pc != null; at += Chunker.Rate / 10)
            {
                Feed("Them", _pcChunks!, all[at..Math.Min(all.Length, at + Chunker.Rate / 10)]);
                Thread.Sleep(100);
            }
            // A little quiet at the end, so the last sentence is finished.
            for (var i = 0; i < 20 && _pc != null; i++) { Feed("Them", _pcChunks!, new float[Chunker.Rate / 10]); Thread.Sleep(100); }
        }) { IsBackground = true };
        _pc = new Stopper();
        t.Start();
        return true;
    });

    sealed class Stopper : IDisposable { public void Dispose() { } }
#endif
}

/// Whatever the PC is playing, from the default speakers or headphones, as 16 kHz mono.
sealed class Loopback : IDisposable
{
    readonly WasapiLoopbackCapture _capture;
    readonly double _step;
    readonly List<float> _carry = [];
    double _t;
    public event Action<float[]>? Heard;

    public Loopback()
    {
        _capture = new WasapiLoopbackCapture();
        var f = _capture.WaveFormat;
        _step = f.SampleRate / (double)Chunker.Rate;
        _capture.DataAvailable += (_, e) =>
        {
            if (e.BytesRecorded == 0) return;
            var mono = Mono(e.Buffer, e.BytesRecorded, f);
            if (mono.Length > 0) Heard?.Invoke(Resample(mono));
        };
        _capture.StartRecording();
    }

    static float[] Mono(byte[] b, int n, WaveFormat f)
    {
        int ch = Math.Max(1, f.Channels), bytes = f.BitsPerSample / 8, frames = n / (bytes * ch);
        var o = new float[frames];
        for (var i = 0; i < frames; i++)
        {
            float sum = 0;
            for (var c = 0; c < ch; c++)
            {
                var at = (i * ch + c) * bytes;
                sum += bytes == 4 ? BitConverter.ToSingle(b, at) : BitConverter.ToInt16(b, at) / 32768f;
            }
            o[i] = sum / ch;
        }
        return o;
    }

    /// Down to 16 kHz, averaging what falls between samples so high sounds don't fold back in.
    float[] Resample(float[] x)
    {
        _carry.AddRange(x);
        var o = new List<float>((int)(_carry.Count / _step) + 1);
        var t = _t;
        while (t + _step <= _carry.Count)
        {
            int from = (int)t, to = Math.Min(_carry.Count, (int)Math.Ceiling(t + _step));
            float sum = 0;
            for (var i = from; i < to; i++) sum += _carry[i];
            o.Add(sum / Math.Max(1, to - from));
            t += _step;
        }
        var used = (int)t;
        _carry.RemoveRange(0, used);
        _t = t - used;
        return [.. o];
    }

    public void Dispose()
    {
        try { _capture.StopRecording(); } catch { }
        _capture.Dispose();
    }
}
