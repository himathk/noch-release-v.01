using System.Text.RegularExpressions;

namespace Noch.Core;

/// Cuts a never-ending stream of 16 kHz sound into stretches of speech, each ending at a pause,
/// so every stretch can be written down on its own.
public sealed class Chunker
{
    public const int Rate = 16000;
    const int Frame = Rate / 50; // 20 ms
    /// Less talking than this in a stretch is a cough or a click, not speech.
    public double MinSeconds { get; init; } = .4;
    public double MaxSeconds { get; init; } = 15;
    /// A pause this long ends a stretch.
    public double PauseSeconds { get; init; } = .8;

    readonly List<float> _speech = [];
    readonly Queue<float[]> _before = new();
    readonly float[] _frame = new float[Frame];
    int _filled, _quiet, _voiced;
    bool _talking;
    double _floor = .002;

    /// How loud the last frame was, 0 to 1 on a speech scale.
    public double Level { get; private set; }

    public List<float[]> Feed(ReadOnlySpan<float> samples)
    {
        var done = new List<float[]>();
        foreach (var x in samples)
        {
            _frame[_filled++] = x;
            if (_filled < Frame) continue;
            _filled = 0;
            if (Step() is { } chunk) done.Add(chunk);
        }
        return done;
    }

    /// Whatever was being said when the listening stopped.
    public float[]? Flush()
    {
        var keep = _talking && _voiced >= MinSeconds * Rate ? _speech.ToArray() : null;
        _speech.Clear(); _talking = false; _quiet = _voiced = 0;
        return keep;
    }

    float[]? Step()
    {
        double sum = 0;
        foreach (var v in _frame) sum += v * v;
        var rms = Math.Sqrt(sum / Frame);
        Level = Math.Clamp((20 * Math.Log10(Math.Max(rms, 1e-6)) + 52) / 40, 0, 1);
        // The room's own hum is learnt while nobody talks, so speech is anything well above it.
        if (!_talking) _floor = Math.Max(.0005, _floor * .97 + rms * .03);
        var voice = rms > Math.Max(_floor * 3, .004);
        var copy = (float[])_frame.Clone();
        if (!_talking)
        {
            // A little from just before, so the first word is not cut off.
            _before.Enqueue(copy);
            if (_before.Count > 15) _before.Dequeue();
            if (!voice) return null;
            _talking = true; _quiet = 0; _voiced = Frame;
            foreach (var f in _before) _speech.AddRange(f);
            _before.Clear();
            return null;
        }
        _speech.AddRange(copy);
        _quiet = voice ? 0 : _quiet + Frame;
        if (voice) _voiced += Frame;
        if (_quiet < PauseSeconds * Rate && _speech.Count < MaxSeconds * Rate) return null;
        return Flush();
    }
}

/// Finds the questions in what was said.
public static partial class Questions
{
    /// Asked all the time in meetings, and never what anyone wants help with.
    static readonly HashSet<string> Filler =
    [
        "right", "okay", "ok", "yeah", "yes", "no", "you know", "can you hear me", "can everyone hear me", "can you see my screen",
        "can everyone see my screen", "does that make sense", "are you there", "how are you", "how are you doing", "sorry",
        "what", "huh", "is that okay", "is that ok", "any questions", "make sense", "does that sound good", "sound good"
    ];

    public static IReadOnlyList<string> In(string text) =>
        [.. Sentences().Split(text).Select(s => s.Trim()).Where(s => s.EndsWith('?') && Words(s) >= 3 && !Filler.Contains(Plain(s)) && !Checking().IsMatch(Plain(s)))];

    static int Words(string s) => s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
    static string Plain(string s) => Regex.Replace(s.ToLowerInvariant(), @"[^a-z' ]", "").Trim();

    [GeneratedRegex(@"(?<=[.?!])\s+")] private static partial Regex Sentences();
    /// Checking the call itself works: can you hear me, can everyone see my screen, is my audio okay.
    [GeneratedRegex(@"^(can|could|are|is|do) (you|everyone|everybody|anyone|we|you all|you guys|they|my) (all )?(still )?(hear|see|able to (hear|see)|audio|mic|screen|video|there)")]
    private static partial Regex Checking();
}
