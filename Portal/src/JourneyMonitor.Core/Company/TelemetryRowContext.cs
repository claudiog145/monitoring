namespace JourneyMonitor.Core.Company;

/// <summary>
/// The fields of one telemetry row that a company profile is allowed to reason about.
///
/// This type exists because the alternative does not work: an adapter handed a pre-built key such
/// as <c>"club:113"</c> cannot execute any <see cref="EntityIdentitySource"/>, since every one of
/// them (a custom dimension, a query parameter, the host, the role name) is a rule for reading a
/// ROW. The pre-built key was one company's convention leaking into the contract, and it would
/// have forced every future company to adopt the same convention or silently resolve nothing.
///
/// Deliberately a flat, provider-neutral shape rather than an Application Insights row: the
/// profile should not know which backend the telemetry came from.
///
/// Nothing derived from <see cref="Url"/> is cached in a field. A record's generated copy
/// constructor copies fields verbatim, so a cached parse would survive <c>row with { Url = ... }</c>
/// and keep answering about the PREVIOUS row - a silent cross-row misattribution, which is exactly
/// the failure this seam exists to prevent.
/// </summary>
public sealed record TelemetryRowContext(
    string? Url = null,
    string? Operation = null,
    string? PageName = null,
    string? RoleName = null,
    IReadOnlyDictionary<string, string>? CustomDimensions = null,
    /// <summary>
    /// Which registered telemetry source this row came from. Load-bearing: a company that serves
    /// several product areas from several resources needs a broad marker in one of them to be
    /// unable to claim another's traffic. Null means the row did not say, and an unknown origin
    /// narrows nothing - guessing would be worse than the ambiguity.
    /// </summary>
    string? SourceAlias = null)
{
    /// <summary>Host of <see cref="Url"/>, lowercased. Null when there is nothing host-shaped to read.</summary>
    public string? Host
    {
        get
        {
            var text = Url?.Trim();
            if (string.IsNullOrEmpty(text)) return null;

            if (Uri.TryCreate(text, UriKind.Absolute, out var absolute) && absolute.Host.Length > 0)
            {
                return absolute.Host.ToLowerInvariant();
            }

            // Telemetry frequently carries an authority with no scheme - "edge.example.test/a" or
            // "//edge.example.test/a". Reading those as "no host" would make every host filter and
            // every host identity source miss, silently.
            if (text.StartsWith("//", StringComparison.Ordinal)) text = text[2..];

            var cut = text.IndexOfAny(['/', '?', '#']);
            if (cut >= 0) text = text[..cut];

            var at = text.LastIndexOf('@');
            if (at >= 0 && at < text.Length - 1) text = text[(at + 1)..];

            var port = text.LastIndexOf(':');
            if (port > 0) text = text[..port];

            return text.Contains('.') ? text.ToLowerInvariant() : null;
        }
    }

    /// <summary>Path portion of <see cref="Url"/>, lowercased, without the query string.</summary>
    public string? Path
    {
        get
        {
            var text = Url?.Trim();
            if (string.IsNullOrEmpty(text)) return null;

            if (Uri.TryCreate(text, UriKind.Absolute, out var absolute))
            {
                return absolute.AbsolutePath.ToLowerInvariant();
            }

            var cut = text.IndexOfAny(['?', '#']);
            if (cut >= 0) text = text[..cut];

            var slash = text.IndexOf('/', text.StartsWith("//", StringComparison.Ordinal) ? 2 : 0);
            return slash >= 0 ? text[slash..].ToLowerInvariant() : null;
        }
    }

    public string? CustomDimension(string key)
    {
        if (CustomDimensions is null || string.IsNullOrWhiteSpace(key)) return null;

        if (CustomDimensions.TryGetValue(key, out var exact) && !string.IsNullOrWhiteSpace(exact)) return exact;

        // Telemetry SDKs disagree about casing for the same logical key (VacationClubId vs
        // VacationClubID is a real example), and a profile author cannot be expected to guess which
        // spelling the SDK emitted.
        foreach (var pair in CustomDimensions)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(pair.Value))
            {
                return pair.Value;
            }
        }

        return null;
    }

    /// <summary>Reads a query parameter straight off the url text, so a scheme-less url still answers.</summary>
    public string? QueryParameter(string name)
    {
        var text = Url;
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(name)) return null;

        var start = text.IndexOf('?');
        if (start < 0 || start == text.Length - 1) return null;

        var query = text[(start + 1)..];
        var fragment = query.IndexOf('#');
        if (fragment >= 0) query = query[..fragment];

        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0) continue;

            var key = Uri.UnescapeDataString(pair[..separator]);
            if (!string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) continue;

            var value = Uri.UnescapeDataString(pair[(separator + 1)..]);
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }

        return null;
    }

    /// <summary>
    /// The text a step matcher tests against for url-shaped kinds. Full url rather than just the
    /// path: a marker can legitimately sit in the query string.
    /// </summary>
    public string UrlText => Url ?? "";

    /// <summary>Path only, so a path marker cannot be satisfied by something in the query string.</summary>
    public string PathText => Path ?? "";

    public string OperationText => Operation ?? "";

    public string PageNameText => PageName ?? "";
}
