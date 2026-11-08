using System.Globalization;
using System.Xml;

namespace FakeAppInsights.Rewrite;

/// <summary>A closed time range, both ends in UTC.</summary>
public sealed record TimeRange(DateTimeOffset From, DateTimeOffset To);

/// <summary>
/// The App Insights REST <c>timespan</c> parameter (contract v5 §3.1): <c>PT6H</c>, <c>P1D</c>
/// (a duration ending now), <c>&lt;start&gt;/&lt;end&gt;</c> and <c>&lt;start&gt;/&lt;duration&gt;</c>.
/// <c>&lt;duration&gt;/&lt;end&gt;</c> is accepted too, as ISO 8601 allows it.
/// </summary>
public static class TimespanParser
{
    public static TimeRange Parse(string iso, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(iso)) throw new FormatException("timespan is empty.");

        var parts = iso.Trim().Split('/');
        TimeRange range;
        switch (parts.Length)
        {
            case 1:
                range = new TimeRange(now - Duration(parts[0]), now);
                break;
            case 2 when IsDuration(parts[0]):
                var end = Instant(parts[1]);
                range = new TimeRange(end - Duration(parts[0]), end);
                break;
            case 2 when IsDuration(parts[1]):
                var start = Instant(parts[0]);
                range = new TimeRange(start, start + Duration(parts[1]));
                break;
            case 2:
                range = new TimeRange(Instant(parts[0]), Instant(parts[1]));
                break;
            default:
                throw new FormatException($"timespan '{iso}' is not an ISO 8601 duration or interval.");
        }

        if (range.From > range.To) throw new FormatException($"timespan '{iso}' ends before it starts.");
        return new TimeRange(range.From.ToUniversalTime(), range.To.ToUniversalTime());
    }

    private static bool IsDuration(string text) => text.TrimStart('-').StartsWith('P');

    private static TimeSpan Duration(string text)
    {
        try
        {
            var value = XmlConvert.ToTimeSpan(text.Trim());
            if (value < TimeSpan.Zero) throw new FormatException($"Negative duration '{text}'.");
            return value;
        }
        catch (FormatException ex)
        {
            throw new FormatException($"'{text}' is not an ISO 8601 duration.", ex);
        }
    }

    private static DateTimeOffset Instant(string text)
    {
        if (DateTimeOffset.TryParse(text.Trim(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value))
        {
            return value;
        }
        throw new FormatException($"'{text}' is not an ISO 8601 instant.");
    }
}
