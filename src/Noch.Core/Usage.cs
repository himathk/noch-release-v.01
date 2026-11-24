using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Noch.Core;

/// One rolling limit on a Claude plan.
public sealed record Limit(double Percent, DateTime ResetsAt);

/// What Claude Code tells its status line. Only the terminal version does this.
public sealed class Usage
{
    readonly object _gate = new();
    Limit? _five, _week;
    readonly Dictionary<string, double> _context = [];
    public event Action? Changed;

    public Limit? FiveHour(DateTime now) { lock (_gate) return Live(_five, now); }
    public Limit? SevenDay(DateTime now) { lock (_gate) return Live(_week, now); }
    /// How full a session's context is, 0 to 100, when Claude Code has said so.
    public double? Context(string sessionKey) { lock (_gate) return _context.TryGetValue(sessionKey, out var p) ? p : null; }
    static Limit? Live(Limit? l, DateTime now) => l != null && l.ResetsAt > now ? l : null;

    /// Takes one status line update and returns the line to show in the terminal.
    public string Take(JsonObject o, DateTime now)
    {
        var parts = new List<string>();
        lock (_gate)
        {
            if (o["rate_limits"] is JsonObject r) { _five = Read(r["five_hour"]) ?? _five; _week = Read(r["seven_day"]) ?? _week; }
            if (Num(o["context_window"]?["used_percentage"]) is { } c)
            {
                if ((string?)o["session_id"] is { } id) _context["claude:" + id] = c;
                parts.Add($"context {c:0}%");
            }
            if (Live(_five, now) is { } f) parts.Add($"5 h {f.Percent:0}%");
            if (Live(_week, now) is { } w) parts.Add($"7 d {w.Percent:0}%");
        }
        Changed?.Invoke();
        return string.Join("   ", parts);
    }

    static double? Num(JsonNode? n) { try { return n?.GetValue<double>(); } catch { return null; } }

    static Limit? Read(JsonNode? n) => Num(n?["used_percentage"]) is { } p && Num(n?["resets_at"]) is { } s
        ? new Limit(Math.Clamp(p, 0, 100), DateTimeOffset.FromUnixTimeSeconds((long)s).UtcDateTime) : null;
}

/// Reads how large a conversation has grown from the end of Claude's own log. Looks at the counts, not the words.
public static class Transcript
{
    const int Tail = 3 * 1024 * 1024;

    public static long? ContextTokens(string? path)
    {
        if (string.IsNullOrEmpty(path) || !path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            f.Seek(-Math.Min(f.Length, Tail), SeekOrigin.End);
            var buf = new byte[f.Length - f.Position];
            f.ReadExactly(buf);
            var end = buf.Length;
            while (end > 0)
            {
                var start = end - 1;
                while (start > 0 && buf[start - 1] != (byte)'\n') start--;
                var line = buf.AsSpan(start, end - start);
                // Cheap test first: most lines are not worth parsing.
                if (line.IndexOf("\"usage\""u8) >= 0 && line.IndexOf("\"assistant\""u8) >= 0 && Read(line) is { } n) return n;
                end = start - 1;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        return null;
    }

    static long? Read(ReadOnlySpan<byte> line)
    {
        try
        {
            var o = JsonNode.Parse(line) as JsonObject;
            if ((string?)o?["type"] != "assistant" || o["isSidechain"]?.GetValue<bool>() == true || o["message"]?["usage"] is not JsonObject u) return null;
            long N(string k) => u[k] is JsonValue v && v.TryGetValue<long>(out var x) ? x : 0;
            var total = N("input_tokens") + N("cache_read_input_tokens") + N("cache_creation_input_tokens") + N("output_tokens");
            return total > 0 ? total : null;
        }
        catch { return null; }
    }

    public static string Short(long n) => n >= 1_000_000 ? $"{n / 1_000_000.0:0.0}M" : n >= 1000 ? $"{n / 1000}k" : n.ToString();
}

/// A day's tally. Kept on this PC only.
public sealed class Day
{
    public string Date { get; set; } = "";
    public int Turns { get; set; }
    public double Seconds { get; set; }
    public int Answers { get; set; }
    public int FocusMinutes { get; set; }
}

public sealed class Stats
{
    readonly object _gate = new();
    readonly string? _path;
    readonly List<Day> _days = [];

    public Stats(string? path = null)
    {
        _path = path;
        try { if (path != null && File.Exists(path)) _days.AddRange((JsonSerializer.Deserialize<List<Day>>(File.ReadAllText(path)) ?? []).Where(d => d != null)); } catch { }
    }

    public Day Today(DateTime localNow) { lock (_gate) return Find(localNow); }
    public IReadOnlyList<Day> Last(int days, DateTime localNow) { lock (_gate) { var from = localNow.AddDays(1 - days).ToString("yyyy-MM-dd"); return [.. _days.Where(d => string.CompareOrdinal(d.Date, from) >= 0)]; } }

    Day Find(DateTime now)
    {
        var key = now.ToString("yyyy-MM-dd");
        var d = _days.FirstOrDefault(x => x.Date == key);
        if (d is null) { _days.Add(d = new Day { Date = key }); if (_days.Count > 90) _days.RemoveRange(0, _days.Count - 90); }
        return d;
    }

    public void Turn(TimeSpan length, DateTime now) => Change(now, d => { d.Turns++; d.Seconds += length.TotalSeconds; });
    public void Answered(DateTime now) => Change(now, d => d.Answers++);
    public void Focused(int minutes, DateTime now) => Change(now, d => d.FocusMinutes += minutes);

    void Change(DateTime now, Action<Day> a)
    {
        lock (_gate)
        {
            a(Find(now));
            try { if (_path != null) { Directory.CreateDirectory(Path.GetDirectoryName(_path)!); File.WriteAllText(_path, JsonSerializer.Serialize(_days)); } }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// "Today: 12 turns, 1 h 40 min of agent work". Empty when there is nothing to say.
    public string Line(DateTime now)
    {
        var d = Today(now);
        var parts = new List<string>();
        if (d.Turns > 0) parts.Add(d.Turns == 1 ? "1 turn" : $"{d.Turns} turns");
        if (d.Seconds >= 60) parts.Add(Span(TimeSpan.FromSeconds(d.Seconds)) + " of agent work");
        if (d.FocusMinutes > 0) parts.Add(Span(TimeSpan.FromMinutes(d.FocusMinutes)) + " focused");
        return parts.Count == 0 ? "" : "Today: " + string.Join(", ", parts);
    }

    public static string Span(TimeSpan t)
    {
        var m = (int)t.TotalMinutes;
        return m < 60 ? $"{m} min" : m % 60 == 0 ? $"{m / 60} h" : $"{m / 60} h {m % 60} min";
    }
}
