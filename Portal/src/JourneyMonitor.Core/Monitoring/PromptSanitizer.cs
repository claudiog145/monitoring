using System.Text;
using System.Text.RegularExpressions;

namespace JourneyMonitor.Core.Monitoring;

/// <summary>
/// Neutralises prompt-injection attempts in telemetry-derived text before it reaches an LLM.
///
/// This matters here specifically because the Scout's prompt is built from request paths,
/// operation names and exception messages - all of which a user can influence by crafting a URL
/// or triggering an error with attacker-chosen text. Detection runs unattended, so there is no
/// human in the loop to notice a hijacked prompt.
///
/// Deliberately applied to the ALREADY-NORMALISED text and never by diffing before/after some
/// other transformation: a format conversion rewrites content by design, so a before/after
/// comparison flags every converted field as an injection.
/// </summary>
public static class PromptSanitizer
{
    private const string Redacted = "[redacted: possible prompt injection]";

    private static readonly Regex[] InjectionPatterns =
    [
        new(@"ignore\s+(all\s+|any\s+|the\s+)*(previous|prior|above|preceding)\s+(instructions?|prompts?|rules?)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"disregard\s+(all\s+|any\s+|the\s+)*(previous|prior|above)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"you\s+are\s+now\s+(a|an|the)\s+", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"(^|\s)(system|assistant|user)\s*:", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"<\|im_(start|end)\|>", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\[/?(system|assistant|inst)\]", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"new\s+instructions?\s*:", RegexOptions.IgnoreCase | RegexOptions.Compiled),
    ];

    /// <summary>
    /// Returns the value with ASCII control characters stripped and truncated to
    /// <paramref name="maxLength"/>, or a redaction marker when it matches a known injection
    /// pattern. An empty/blank value becomes <paramref name="whenEmpty"/> so the prompt never
    /// contains a dangling label with nothing after it.
    /// </summary>
    public static string Sanitize(string? value, int maxLength = 500, string whenEmpty = "(none)")
    {
        if (string.IsNullOrWhiteSpace(value)) return whenEmpty;

        var sb = new StringBuilder(Math.Min(value.Length, maxLength + 1));
        foreach (var c in value)
        {
            if (sb.Length >= maxLength) break;
            // Keep newline/tab - they carry real structure in stack traces and messages.
            if (char.IsControl(c) && c != '\n' && c != '\t') continue;
            sb.Append(c);
        }

        var cleaned = sb.ToString().Trim();
        if (cleaned.Length == 0) return whenEmpty;

        foreach (var pattern in InjectionPatterns)
        {
            if (pattern.IsMatch(cleaned)) return Redacted;
        }

        if (value.Length > maxLength) cleaned += " …[truncated]";
        return cleaned;
    }

    /// <summary>Sanitizes each item and joins them, so one poisoned path cannot smuggle instructions in through a list.</summary>
    public static string SanitizeList(IEnumerable<string?> values, int take = 10, int maxItemLength = 160, string whenEmpty = "(none)")
    {
        var items = values
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Take(take)
            .Select(v => Sanitize(v, maxItemLength, whenEmpty: ""))
            .Where(v => v.Length > 0)
            .ToList();

        return items.Count == 0 ? whenEmpty : string.Join(", ", items);
    }
}
