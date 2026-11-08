using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using JourneyMonitor.Core.Company;

namespace JourneyMonitor.Core.Monitoring.Privacy;

/// <summary>
/// Contract v4 §5: hides personal data before evidence reaches a model.
///
/// Policy, decided per profile:
/// <list type="bullet">
/// <item><c>Privacy.AllowRawEvidence = true</c>: the text passes through untouched.</item>
/// <item><c>Privacy</c> absent on the incumbent profile (<see cref="KpiProfiles.Default"/>): untouched too,
/// so the incumbent keeps the behaviour it had before profiles could declare privacy.</item>
/// <item>Every other case (any other profile without a Privacy block, or a block with
/// <c>AllowRawEvidence = false</c>): closed by default - built-in patterns, the profile's
/// <c>PiiDimensions</c> and its extra <c>Patterns</c> are all masked.</item>
/// </list>
///
/// Replacements are deterministic tokens (<c>[REDACTED:email]</c>, <c>[REDACTED:pii]</c>, ...), and
/// <see cref="RedactionResult.CountsByPattern"/> only ever carries pattern names and counts.
/// A pattern that times out fails closed: the whole text is replaced by <see cref="UnprocessableToken"/>.
///
/// Known trade-offs, deliberate for a closed-by-default filter: any Luhn-valid run of 13-19 digits is
/// treated as a card (so roughly one in ten epoch-millisecond timestamps or long numeric ids is masked
/// too), and a dotted quad is treated as an IP address unless it is the value of a version key or
/// follows <c>Version=</c> - release versions must survive, the model needs them to tie a regression
/// to a deployment.
/// </summary>
public sealed class EvidenceRedactor : IEvidenceRedactor
{
    public const string UnprocessableToken = "[REDACTED:unprocessable]";
    public const string PiiToken = "[REDACTED:pii]";

    /// <summary>Count keys of the built-in patterns. Profile patterns count under <c>pattern:{index}</c>, dimensions under <c>pii:{name}</c>.</summary>
    public static class Keys
    {
        public const string Bearer = "bearer";
        public const string Jwt = "jwt";
        public const string Email = "email";
        public const string Card = "card";
        public const string Ssn = "ssn";
        public const string Phone = "phone";
        public const string Ipv4 = "ipv4";
        public const string Unprocessable = "unprocessable";

        public static string Pattern(int index) => $"pattern:{index}";

        public static string Dimension(string name) => $"pii:{name.Trim()}";
    }

    internal static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    private const RegexOptions BuiltInOptions = RegexOptions.CultureInvariant | RegexOptions.Compiled;

    private static readonly IReadOnlyList<(string Key, Regex Regex)> BuiltIns =
    [
        (Keys.Bearer, new Regex(@"(?i)\bbearer\s+[A-Za-z0-9\-._~+/]+=*", BuiltInOptions, MatchTimeout)),
        (Keys.Jwt, new Regex(@"\beyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]*", BuiltInOptions, MatchTimeout)),
        (Keys.Email, new Regex(@"[\p{L}\p{N}._%+-]+@[\p{L}\p{N}-]+(?:\.[\p{L}\p{N}-]+)*\.\p{L}{2,}", BuiltInOptions, MatchTimeout)),
        (Keys.Card, new Regex(@"(?<![\w-])(?:\d[ -]?){12,18}\d(?![\w-])", BuiltInOptions, MatchTimeout)),
        (Keys.Ssn, new Regex(@"(?<![\w-])\d{3}-\d{2}-\d{4}(?![\w-])", BuiltInOptions, MatchTimeout)),
        (Keys.Phone, new Regex(@"(?<![\w+.-])(?:\+\d{1,3}[\s.-]?)?(?:\(\d{3}\)\s?|\d{3}[\s.-])\d{3}[\s.-]\d{4}(?![\w-])|(?<![\w+])\+\d{1,3}[\s.-]?\d{2,4}[\s.-]?\d{3,4}[\s.-]?\d{3,4}(?![\w-])", BuiltInOptions, MatchTimeout)),
        (Keys.Ipv4, new Regex(@"(?<![\w.])(?:(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)\.){3}(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)(?![\w.])", BuiltInOptions, MatchTimeout)),
    ];

    /// <summary>
    /// What a dotted quad that is really a version looks like just before it: the value of a key whose
    /// name contains "version" (<c>"appVersion":"1.0.0.0"</c>, <c>application_Version=1.0.0.0</c>,
    /// <c>Version=8.0.0.0</c> in an assembly-qualified type name) or prose such as "version 1.2.3.4".
    /// </summary>
    private static readonly Regex VersionContext = new(
        @"(?i)(?:version[\w.-]*\\?[""']?\s*[:=]\s*\\?[""']?|\bversion\s+)$",
        BuiltInOptions, MatchTimeout);

    private const int VersionContextWindow = 64;

    private static readonly IReadOnlyDictionary<string, int> NoCounts = new Dictionary<string, int>();

    private readonly ConcurrentDictionary<string, Regex?> _profilePatterns = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Regex> _dimensionPatterns = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="profile"/> must have its evidence redacted. See the class remarks.</summary>
    public static bool RedactionRequired(CompanyProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Privacy is { } privacy) return !privacy.AllowRawEvidence;
        return !string.Equals(profile.ProfileKey?.Trim(), KpiProfiles.Default, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The profile to redact for when the store has none (or no store is wired): only the key is
    /// known, so no Privacy block, which keeps the incumbent untouched and closes every other key.
    /// </summary>
    public static CompanyProfile PlaceholderProfile(string? profileKey)
    {
        var key = string.IsNullOrWhiteSpace(profileKey) ? KpiProfiles.Default : profileKey.Trim();
        return new CompanyProfile(
            key,
            key,
            new CompanyLabels("Service", "Flow", "Step", "Entity", "Owner"),
            [],
            [],
            new CompanyGlossary("service", "flow", "step", "entity", "owner"));
    }

    /// <summary>Adds <paramref name="counts"/> into <paramref name="total"/>.</summary>
    public static void Accumulate(IDictionary<string, int> total, IReadOnlyDictionary<string, int> counts)
    {
        ArgumentNullException.ThrowIfNull(total);
        ArgumentNullException.ThrowIfNull(counts);
        foreach (var (key, count) in counts)
        {
            total[key] = total.TryGetValue(key, out var existing) ? existing + count : count;
        }
    }

    public RedactionResult Redact(string text, CompanyProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (string.IsNullOrEmpty(text) || !RedactionRequired(profile)) return new RedactionResult(text ?? "", NoCounts);

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        try
        {
            var current = text;
            foreach (var dimension in profile.Privacy?.PiiDimensions ?? [])
            {
                if (string.IsNullOrWhiteSpace(dimension)) continue;
                current = RedactDimension(current, dimension.Trim(), counts);
            }

            var patterns = profile.Privacy?.Patterns ?? [];
            for (var i = 0; i < patterns.Count; i++)
            {
                var regex = ProfilePattern(patterns[i]);
                if (regex is null) continue;
                current = Replace(current, regex, Keys.Pattern(i), $"[REDACTED:pattern:{i}]", counts);
            }

            foreach (var (key, regex) in BuiltIns)
            {
                current = key switch
                {
                    Keys.Card => ReplaceCards(current, regex, counts),
                    Keys.Ipv4 => ReplaceIpv4(current, regex, counts),
                    _ => Replace(current, regex, key, $"[REDACTED:{key}]", counts),
                };
            }

            return new RedactionResult(current, counts);
        }
        catch (RegexMatchTimeoutException)
        {
            return new RedactionResult(UnprocessableToken, new Dictionary<string, int>(StringComparer.Ordinal) { [Keys.Unprocessable] = 1 });
        }
    }

    private string RedactDimension(string text, string dimension, Dictionary<string, int> counts)
    {
        var regex = _dimensionPatterns.GetOrAdd(dimension, BuildDimensionPattern);
        var key = Keys.Dimension(dimension);
        var hits = 0;
        var result = regex.Replace(text, m =>
        {
            hits++;
            var quote = m.Groups["q"].Success ? m.Groups["q"].Value : "";
            return m.Groups["pre"].Value + quote + PiiToken + quote;
        });
        if (hits > 0) counts[key] = (counts.TryGetValue(key, out var existing) ? existing : 0) + hits;
        return result;
    }

    /// <summary>
    /// <c>name</c> followed by <c>:</c> or <c>=</c> and a value - plain JSON, JSON escaped inside a
    /// string (<c>\"name\":\"value\"</c>), single-quoted (<c>name='First Last'</c>), or unquoted as in a
    /// query string, a KQL bag dump or a log line. A quoted value is masked whole, spaces included; an
    /// unquoted one runs to the next delimiter (<c>, ; &amp; |</c>, a closing brace/bracket or the end
    /// of the line), so <c>FullName: Jane Doe</c> loses the surname too.
    /// </summary>
    private static Regex BuildDimensionPattern(string dimension)
    {
        var name = Regex.Escape(dimension);
        var pattern =
            $@"(?<pre>(?<![\w]){name}(?:\\?[""'])?\s*[:=]\s*)(?!(?:\\?[""'])?\[REDACTED:)" +
            @"(?:(?<q>\\?[""'])(?<v>(?:(?!\k<q>)[^\r\n])*)\k<q>" +
            @"|(?!null\b)(?<v>[^\s,;&|}\]""'\\](?:[^,;&|\r\n}\]""\\]*[^\s,;&|}\]""\\])?))";
        return new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, MatchTimeout);
    }

    private Regex? ProfilePattern(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return null;
        return _profilePatterns.GetOrAdd(pattern, p =>
        {
            try
            {
                return new Regex(p, RegexOptions.CultureInvariant, MatchTimeout);
            }
            catch (ArgumentException)
            {
                // CompanyProfileValidator rejects these on save; a stored one is skipped rather than failing the prompt.
                return null;
            }
        });
    }

    private static string Replace(string text, Regex regex, string key, string token, Dictionary<string, int> counts)
    {
        var hits = 0;
        var result = regex.Replace(text, m =>
        {
            if (m.Length == 0) return m.Value;
            hits++;
            return token;
        });
        if (hits > 0) counts[key] = (counts.TryGetValue(key, out var existing) ? existing : 0) + hits;
        return result;
    }

    private static string ReplaceCards(string text, Regex regex, Dictionary<string, int> counts)
    {
        var hits = 0;
        var result = regex.Replace(text, m =>
        {
            if (!PassesLuhn(m.Value)) return m.Value;
            hits++;
            return $"[REDACTED:{Keys.Card}]";
        });
        if (hits > 0) counts[Keys.Card] = (counts.TryGetValue(Keys.Card, out var existing) ? existing : 0) + hits;
        return result;
    }

    private static string ReplaceIpv4(string text, Regex regex, Dictionary<string, int> counts)
    {
        var hits = 0;
        var result = regex.Replace(text, m =>
        {
            var start = Math.Max(0, m.Index - VersionContextWindow);
            var before = text[start..m.Index];
            var lineBreak = before.LastIndexOfAny(['\r', '\n']);
            if (lineBreak >= 0) before = before[(lineBreak + 1)..];
            if (VersionContext.IsMatch(before)) return m.Value;
            hits++;
            return $"[REDACTED:{Keys.Ipv4}]";
        });
        if (hits > 0) counts[Keys.Ipv4] = (counts.TryGetValue(Keys.Ipv4, out var existing) ? existing : 0) + hits;
        return result;
    }

    internal static bool PassesLuhn(string candidate)
    {
        var digits = candidate.Where(char.IsAsciiDigit).Select(c => c - '0').ToArray();
        if (digits.Length is < 13 or > 19) return false;

        var sum = 0;
        var doubleIt = false;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var d = digits[i];
            if (doubleIt)
            {
                d *= 2;
                if (d > 9) d -= 9;
            }
            sum += d;
            doubleIt = !doubleIt;
        }
        return sum % 10 == 0;
    }
}
