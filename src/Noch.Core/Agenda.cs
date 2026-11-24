using System.Text.RegularExpressions;
using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;

namespace Noch.Core;

public sealed record Meeting(string Id, string Title, DateTime Start, DateTime End, string? Link)
{
    /// "in 24 min", "now", "in 2 h 5 min".
    public string When(DateTime now)
    {
        var left = Start - now;
        if (left <= TimeSpan.Zero) return now < End ? "now" : "over";
        var m = (int)Math.Ceiling(left.TotalMinutes);
        return m < 60 ? $"in {m} min" : m % 60 == 0 ? $"in {m / 60} h" : $"in {m / 60} h {m % 60} min";
    }
}

/// Reads a calendar feed (.ics). Times are UTC.
public static partial class Agenda
{
    /// Timed events between two moments, soonest first. Repeating ones are expanded.
    public static IReadOnlyList<Meeting> Parse(string ics, DateTime from, DateTime to)
    {
        var list = new List<Meeting>();
        foreach (var cal in CalendarCollection.Load(ics))
        {
            foreach (var o in cal.GetOccurrences<CalendarEvent>(new CalDateTime(from.AddDays(-1))).TakeWhileBefore(new CalDateTime(to)).Take(2000))
            {
                if (o.Source is not CalendarEvent e || e.IsAllDay || string.Equals(e.Status, "CANCELLED", StringComparison.OrdinalIgnoreCase)) continue;
                var start = o.Period.StartTime.AsUtc;
                var end = o.Period.EffectiveEndTime?.AsUtc ?? start.AddMinutes(30);
                if (end <= from || start >= to) continue;
                list.Add(new Meeting($"{e.Uid}|{start:O}", string.IsNullOrWhiteSpace(e.Summary) ? "Busy" : Describe.OneLine(e.Summary, 120), start, end, FindLink(e)));
            }
        }
        return [.. list.DistinctBy(m => m.Id).OrderBy(m => m.Start)];
    }

    static string? FindLink(CalendarEvent e) =>
        Link(e.Location) ?? Link(e.Properties.Get<string>("X-GOOGLE-CONFERENCE")) ?? Link(e.Url?.ToString()) ?? Link(e.Description);

    /// The first address that looks like a video call.
    public static string? Link(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        foreach (Match m in Web().Matches(text))
        {
            var s = m.Value.TrimEnd('.', ',', ';', ')', ']', '>', '"', '\'');
            if (!Uri.TryCreate(s, UriKind.Absolute, out var u)) continue;
            if (Hosts.Any(h => u.Host.Equals(h, StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase))) return u.AbsoluteUri;
        }
        return null;
    }

    static readonly string[] Hosts = ["meet.google.com", "zoom.us", "zoom.com", "teams.microsoft.com", "teams.live.com", "webex.com", "whereby.com", "meet.jit.si", "discord.gg", "gotomeeting.com", "chime.aws", "around.co"];

    [GeneratedRegex(@"https://[^\s<>""']+", RegexOptions.IgnoreCase)]
    private static partial Regex Web();

    /// Turns what people paste into an address to fetch, or null if it is not one.
    public static Uri? Feed(string? pasted)
    {
        var s = pasted?.Trim() ?? "";
        if (s.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase)) s = "https://" + s[9..];
        return Uri.TryCreate(s, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps ? u : null;
    }
}
