using System.Text;
using System.Text.RegularExpressions;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JourneyMonitor.Core.UseCases;

/// <summary>
/// Which company profile may reference a telemetry source alias (monitoring.TelemetrySource.OwnerProfileKey).
///
/// Scouts, KPIs and kql: queries all trust the aliases a profile's services declare, so the alias
/// list is where one company could otherwise read another's telemetry. The check runs when a
/// profile is SAVED, against the catalog, rather than in the pure profile validators: those judge
/// the document alone and must stay free of storage.
/// </summary>
public static class TelemetrySourceOwnership
{
    public static string AliasOwnedByAnotherProfileMessage(string alias, string owner) =>
        $"Alias '{alias}' belongs to profile '{owner}'.";

    public static string AliasNotInCatalogMessage(string alias) =>
        $"Alias '{alias}' is not in the telemetry source catalog.";

    public static string SourceOwnedByAnotherProfileMessage(string alias, string? owner) =>
        owner is null
            ? $"Source '{alias}' is shared; send transferOwnerTo to give it an owner."
            : $"Source '{alias}' belongs to profile '{owner}'.";

    public static string UnknownProfileMessage(string profileKey) => $"Unknown profileKey '{profileKey}'.";

    /// <summary>
    /// One message per alias of <paramref name="profile"/> that the profile may not use: an alias
    /// whose catalog row (in any environment) is owned by a different profile, or one the catalog
    /// does not know at all - otherwise a profile could pre-claim an alias that another company
    /// registers later and read it from the moment it appears. Shared rows (null owner) are allowed.
    /// An empty catalog falls back to <see cref="TelemetrySourceDefaults"/>, as the resolver does.
    /// </summary>
    public static IReadOnlyList<string> FindForeignAliases(CompanyProfile profile, IReadOnlyList<TelemetrySourceEntry> catalog)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(catalog);

        var effective = catalog.Count > 0 ? catalog : TelemetrySourceDefaults.All;
        var profileKey = (profile.ProfileKey ?? "").Trim();
        var problems = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var alias in (profile.Services ?? []).SelectMany(s => s?.TelemetrySourceAliases ?? []))
        {
            var normalized = (alias ?? "").Trim();
            if (normalized.Length == 0 || !seen.Add(normalized)) continue;

            var rows = effective
                .Where(s => string.Equals(s.Alias?.Trim(), normalized, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (rows.Count == 0)
            {
                problems.Add(AliasNotInCatalogMessage(normalized));
                continue;
            }

            var owner = rows
                .Select(s => s.OwnerProfileKey?.Trim())
                .FirstOrDefault(o => !string.IsNullOrEmpty(o) && !string.Equals(o, profileKey, StringComparison.OrdinalIgnoreCase));
            if (owner is not null)
            {
                problems.Add(AliasOwnedByAnotherProfileMessage(normalized, owner));
            }
        }

        return problems;
    }

    /// <summary>
    /// Read-time rule (FX1): the profile other than <paramref name="readingProfileKey"/> that owns
    /// <paramref name="alias"/> in any environment of <paramref name="catalog"/>, or null when the
    /// reader may use it (owned by the reader, shared, or unknown - an unknown alias fails resolution
    /// on its own). The same alias grain as the save-time check.
    /// </summary>
    public static string? ForeignOwnerOf(IReadOnlyList<TelemetrySourceEntry> catalog, string? alias, string? readingProfileKey)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var normalized = (alias ?? "").Trim();
        if (normalized.Length == 0) return null;
        var reader = (readingProfileKey ?? "").Trim();

        return catalog
            .Where(s => string.Equals(s.Alias?.Trim(), normalized, StringComparison.OrdinalIgnoreCase))
            .Select(s => s.OwnerProfileKey?.Trim())
            .FirstOrDefault(o => !string.IsNullOrEmpty(o) && !string.Equals(o, reader, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Every distinct alias of <paramref name="aliases"/> that <paramref name="readingProfileKey"/> may not read, with its owner.</summary>
    public static IReadOnlyList<ForeignAliasOwner> FindForeignOwners(
        IReadOnlyList<TelemetrySourceEntry> catalog, string? readingProfileKey, IEnumerable<string?> aliases)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(aliases);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var foreign = new List<ForeignAliasOwner>();
        foreach (var alias in aliases)
        {
            var normalized = (alias ?? "").Trim();
            if (normalized.Length == 0 || !seen.Add(normalized)) continue;
            var owner = ForeignOwnerOf(catalog, normalized, readingProfileKey);
            if (owner is not null) foreign.Add(new ForeignAliasOwner(normalized, owner));
        }
        return foreign;
    }

    /// <inheritdoc cref="FindForeignAliases(CompanyProfile, IReadOnlyList{TelemetrySourceEntry})"/>
    public static async Task<IReadOnlyList<string>> FindForeignAliasesAsync(
        CompanyProfile profile, ITelemetrySourceStore catalog, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return FindForeignAliases(profile, await catalog.GetAllAsync(ct).ConfigureAwait(false));
    }
}

/// <summary>An alias a reading profile may not use, and the profile that owns it.</summary>
public sealed record ForeignAliasOwner(string Alias, string Owner)
{
    public string Message => TelemetrySourceOwnership.AliasOwnedByAnotherProfileMessage(Alias, Owner);
}

/// <summary>
/// The read-time half of alias ownership (FX1). Saving a profile already refuses a foreign alias,
/// but a transfer moves an alias AFTER profiles were saved; every resolution (KPI sources, kql:
/// queries and their dry run, the Scout board) therefore asks this check before reading an alias.
/// The catalog is cached for <see cref="CacheTtl"/> so a fetch does not hit the database each
/// time, which is also the longest a previous owner keeps reading after a transfer.
/// </summary>
public sealed class TelemetrySourceOwnershipCheck(
    ITelemetrySourceStore catalog,
    TimeProvider? clock = null,
    ILogger<TelemetrySourceOwnershipCheck>? logger = null)
{
    public static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ILogger<TelemetrySourceOwnershipCheck> _logger = logger ?? NullLogger<TelemetrySourceOwnershipCheck>.Instance;

    // Reference assignment is atomic; concurrent readers can at worst both refresh.
    private Snapshot? _snapshot;

    /// <summary>
    /// The catalog (the built-in sources when the table is empty, as <see cref="KqlSourceResolver"/>
    /// does), at most <see cref="CacheTtl"/> old. A read failure is NOT cached and propagates.
    /// </summary>
    public async Task<IReadOnlyList<TelemetrySourceEntry>> GetCatalogAsync(CancellationToken ct = default)
    {
        var cached = _snapshot;
        var now = _clock.GetUtcNow();
        if (cached is not null && cached.ExpiresAt > now) return cached.Sources;

        var rows = await catalog.GetAllAsync(ct).ConfigureAwait(false);
        var effective = rows.Count > 0 ? rows : TelemetrySourceDefaults.All;
        _snapshot = new Snapshot(effective, now.Add(CacheTtl));
        return effective;
    }

    /// <summary>The aliases <paramref name="readingProfileKey"/> may not read. A catalog read failure propagates.</summary>
    public async Task<IReadOnlyList<ForeignAliasOwner>> FindForeignAsync(
        string? readingProfileKey, IEnumerable<string?> aliases, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(aliases);
        var list = aliases.ToList();
        if (list.All(a => string.IsNullOrWhiteSpace(a))) return [];
        return TelemetrySourceOwnership.FindForeignOwners(await GetCatalogAsync(ct).ConfigureAwait(false), readingProfileKey, list);
    }

    /// <summary>
    /// <see cref="FindForeignAsync"/> for callers whose resolver already survives a catalog outage on
    /// the built-in sources (the Scout board): on a read failure the check runs against the same
    /// built-in sources, so it never blocks more or less than the resolver would resolve.
    /// </summary>
    public async Task<IReadOnlyList<ForeignAliasOwner>> FindForeignOrBuiltInAsync(
        string? readingProfileKey, IEnumerable<string?> aliases, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(aliases);
        var list = aliases.ToList();
        try
        {
            return await FindForeignAsync(readingProfileKey, list, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[TelemetrySource] catalog could not be read for the ownership check - checking against the built-in sources.");
            return TelemetrySourceOwnership.FindForeignOwners(TelemetrySourceDefaults.All, readingProfileKey, list);
        }
    }

    /// <summary>Drops the cached catalog, so an edit or a transfer applies immediately.</summary>
    public void Invalidate() => _snapshot = null;

    private sealed record Snapshot(IReadOnlyList<TelemetrySourceEntry> Sources, DateTimeOffset ExpiresAt);
}

/// <summary>
/// The telemetry sources one investigation may read (FX4). <see cref="IsIncumbent"/> keeps the
/// incumbent's fixed drill-down/Inspector queries exactly as they are; any other profile reads only
/// <see cref="Aliases"/> - its own services' aliases that pass <see cref="TelemetrySourceOwnershipCheck"/>.
/// An empty list means "do not query", and <see cref="Reason"/> says why.
/// </summary>
public sealed record ProfileTelemetrySources(string ProfileKey, bool IsIncumbent, IReadOnlyList<string> Aliases, string? Reason)
{
    public static string NoSourcesMessage(string profileKey) =>
        $"No telemetry sources configured for profile '{profileKey}'.";

    public bool CanQuery => IsIncumbent || Aliases.Count > 0;

    public static ProfileTelemetrySources Incumbent() => new(KpiProfiles.Default, true, [], null);

    public static ProfileTelemetrySources None(string profileKey, string? reason = null) =>
        new(profileKey, false, [], reason ?? NoSourcesMessage(profileKey));

    /// <summary>
    /// <paramref name="kql"/> as this profile may run it: unchanged for the incumbent, otherwise
    /// scoped by <see cref="TelemetrySourceScope.Restrict"/>.
    /// </summary>
    public string Apply(string kql) => IsIncumbent ? kql : TelemetrySourceScope.Restrict(kql, Aliases);
}

/// <summary>
/// Rewrites a query written over the incumbent's fixed sources so it reads another profile's sources
/// instead. Every <c>union</c> block of one-line <c>(app("{{SOURCE:alias}}")...)</c> branches becomes one
/// branch per allowed alias, in the shape <see cref="CompanyKqlTemplater.RenderRequestSourceUnion"/>
/// uses for the boards: the first branch is the template and <c>Source</c> is the alias itself.
/// </summary>
public static class TelemetrySourceScope
{
    private static readonly Regex Branch = new(
        @"^(?<indent>\s*)\(app\(""\{\{SOURCE:(?<alias>[^}]+)\}\}""\)(?<body>.*)\)(?<comma>,?)\s*$",
        RegexOptions.Compiled);

    private static readonly Regex SourceLabel = new(@"Source=""[^""]*""", RegexOptions.Compiled);

    private static readonly Regex AnySource = new(@"\{\{SOURCE:(?<alias>[^}]*)\}\}", RegexOptions.Compiled);

    private static readonly Regex AliasPattern = new(@"^[A-Za-z0-9._-]{1,64}$", RegexOptions.Compiled);

    /// <summary>
    /// Throws when <paramref name="aliases"/> is empty, when an alias is not resolvable, or when a
    /// <c>{{SOURCE:}}</c> of the template sits outside a rewritable branch: running such a query
    /// would still read a source the profile does not own.
    /// </summary>
    public static string Restrict(string kql, IReadOnlyList<string> aliases)
    {
        ArgumentNullException.ThrowIfNull(kql);
        ArgumentNullException.ThrowIfNull(aliases);
        var allowed = aliases.Select(a => (a ?? "").Trim()).Where(a => a.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (allowed.Count == 0) throw new InvalidOperationException("No telemetry source alias to scope the query to.");
        foreach (var alias in allowed)
        {
            if (!AliasPattern.IsMatch(alias)) throw new InvalidOperationException($"Telemetry source alias \"{alias}\" is not resolvable.");
        }

        var lines = kql.Split('\n');
        var output = new StringBuilder(kql.Length);
        var i = 0;
        while (i < lines.Length)
        {
            var first = Branch.Match(lines[i].TrimEnd('\r'));
            if (!first.Success)
            {
                output.Append(lines[i]);
                if (i < lines.Length - 1) output.Append('\n');
                i++;
                continue;
            }

            var end = i;
            while (end + 1 < lines.Length && Branch.IsMatch(lines[end + 1].TrimEnd('\r'))) end++;
            var lastLine = lines[end];
            var lineEnd = lastLine.EndsWith('\r') ? "\r" : "";
            var trailingComma = Branch.Match(lastLine.TrimEnd('\r')).Groups["comma"].Value;
            var branchEnd = lines[i].EndsWith('\r') ? "\r\n" : "\n";

            for (var a = 0; a < allowed.Count; a++)
            {
                var alias = allowed[a];
                var body = SourceLabel.Replace(first.Groups["body"].Value, _ => "Source=" + CompanyKqlTemplater.Literal(alias));
                output.Append(first.Groups["indent"].Value)
                    .Append("(app(\"{{SOURCE:").Append(alias).Append("}}\")").Append(body).Append(')');
                if (a < allowed.Count - 1)
                {
                    output.Append(',').Append(branchEnd);
                }
                else
                {
                    output.Append(trailingComma).Append(lineEnd);
                }
            }

            if (end < lines.Length - 1) output.Append('\n');
            i = end + 1;
        }

        var result = output.ToString();
        var foreign = AnySource.Matches(result).Select(m => m.Groups["alias"].Value.Trim())
            .FirstOrDefault(a => !allowed.Contains(a, StringComparer.OrdinalIgnoreCase));
        if (foreign is not null)
        {
            throw new InvalidOperationException(
                $"The query references telemetry source \"{foreign}\" outside a source union, so it cannot be scoped to this profile.");
        }
        return result;
    }
}

/// <summary>
/// Resolves <see cref="ProfileTelemetrySources"/> for the Scout drill-down and the Inspector's log
/// tools (FX4). Fails closed: an unknown profile, a profile without aliases, or a catalog that cannot
/// be read all mean "no query" for a non-incumbent profile.
/// </summary>
public sealed class ProfileTelemetrySourceResolver(
    ICompanyProfileStore? profiles,
    TelemetrySourceOwnershipCheck ownership,
    ILogger<ProfileTelemetrySourceResolver>? logger = null)
{
    public static bool IsIncumbent(string? profileKey) =>
        string.IsNullOrWhiteSpace(profileKey) || string.Equals(profileKey.Trim(), KpiProfiles.Default, StringComparison.OrdinalIgnoreCase);

    public async Task<ProfileTelemetrySources> ResolveAsync(string? profileKey, CancellationToken ct = default)
    {
        if (IsIncumbent(profileKey)) return ProfileTelemetrySources.Incumbent();
        var key = profileKey!.Trim();
        if (profiles is null) return ProfileTelemetrySources.None(key);

        try
        {
            var profile = await profiles.GetAsync(key, ct).ConfigureAwait(false);
            if (profile is null) return ProfileTelemetrySources.None(key);

            var declared = (profile.Services ?? [])
                .SelectMany(s => s?.TelemetrySourceAliases ?? [])
                .Select(a => (a ?? "").Trim())
                .Where(a => a.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (declared.Count == 0) return ProfileTelemetrySources.None(key);

            var foreign = await ownership.FindForeignAsync(key, declared, ct).ConfigureAwait(false);
            foreach (var f in foreign)
            {
                (logger ?? (ILogger)NullLogger.Instance).LogWarning(
                    "[TelemetrySource] Profile \"{Profile}\" skips telemetry source \"{Alias}\": it belongs to profile \"{Owner}\".",
                    key, f.Alias, f.Owner);
            }

            var skipped = new HashSet<string>(foreign.Select(f => f.Alias), StringComparer.OrdinalIgnoreCase);
            var allowed = declared.Where(a => !skipped.Contains(a)).ToList();
            return allowed.Count == 0
                ? ProfileTelemetrySources.None(key)
                : new ProfileTelemetrySources(key, false, allowed, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            (logger ?? (ILogger)NullLogger.Instance).LogWarning(ex,
                "[TelemetrySource] the telemetry sources of profile \"{Profile}\" could not be resolved - nothing is queried.", key);
            return ProfileTelemetrySources.None(key,
                $"The telemetry sources of profile '{key}' could not be verified, so nothing was queried.");
        }
    }
}

/// <summary>
/// The catalog-backed ownership check a company profile save path runs before storing a profile.
/// Hosts register it next to the catalog; see <see cref="TelemetrySourceOwnership"/>.
/// </summary>
public sealed class TelemetrySourceOwnershipGuard(ITelemetrySourceStore catalog)
{
    /// <summary>One message per alias the profile may not use; empty when the profile may be saved.</summary>
    public Task<IReadOnlyList<string>> FindForeignAliasesAsync(CompanyProfile profile, CancellationToken ct = default) =>
        TelemetrySourceOwnership.FindForeignAliasesAsync(profile, catalog, ct);
}

/// <summary>The owner a telemetry source write stores, or why the write is refused.</summary>
/// <param name="StatusCode">200 when <see cref="Error"/> is null; otherwise 400 or 409.</param>
/// <param name="AlsoMove">On an explicit transfer: the alias's rows in OTHER environments, which move with it.</param>
public sealed record TelemetrySourceOwnerDecision(
    int StatusCode,
    string? OwnerProfileKey,
    string? Error,
    IReadOnlyList<TelemetrySourceEntry>? AlsoMove = null)
{
    public bool IsAllowed => Error is null;
}

/// <summary>
/// Decides who owns a telemetry source on write. Ownership belongs to the ALIAS, across every
/// environment - the same grain the profile save check uses - and never moves implicitly:
/// <list type="bullet">
/// <item>An edit keeps the stored owner. A row for a new environment of an existing alias inherits the alias's owner.</item>
/// <item>Naming (profileKey) a profile other than the alias's owner is a 409, whichever environment is written.</item>
/// <item>An alias with no rows left keeps the last owner its audit history records, so deleting and
/// recreating it does not free it for somebody else.</item>
/// <item>A brand-new alias is owned by the named profileKey, else the active profile; that profile must exist (400).</item>
/// <item>Only an explicit <c>transferOwnerTo</c> naming an existing profile moves an alias, in every environment.</item>
/// </list>
/// Without a profile store (bare hosts) only the incumbent and the active profile are known, as in <c>KpiProfileResolver</c>.
/// </summary>
public sealed class TelemetrySourceOwnerPolicy(
    ICompanyProfileStore? profiles = null,
    Monitoring.Kpi.KpiProfileResolver? activeProfile = null)
{
    public async Task<TelemetrySourceOwnerDecision> DecideUpsertAsync(
        ITelemetrySourceStore catalog,
        string alias,
        string environment,
        string? requestedProfileKey,
        string? transferOwnerTo,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var requested = Clean(requestedProfileKey);
        var transfer = Clean(transferOwnerTo);
        var aliasRows = (await catalog.GetAllAsync(ct).ConfigureAwait(false))
            .Where(s => string.Equals(s.Alias?.Trim(), alias, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var existing = aliasRows.FirstOrDefault(s => string.Equals(s.Environment?.Trim(), environment, StringComparison.OrdinalIgnoreCase));

        if (transfer is not null)
        {
            var target = await FindProfileAsync(transfer, ct).ConfigureAwait(false);
            return target is null
                ? new TelemetrySourceOwnerDecision(400, null, TelemetrySourceOwnership.UnknownProfileMessage(transfer))
                : new TelemetrySourceOwnerDecision(200, target, null, aliasRows.Where(r => !ReferenceEquals(r, existing)).ToList());
        }

        if (aliasRows.Count > 0)
        {
            var owners = aliasRows.Select(r => Clean(r.OwnerProfileKey)).OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (requested is not null)
            {
                var foreign = owners.FirstOrDefault(o => !string.Equals(o, requested, StringComparison.OrdinalIgnoreCase));
                if (foreign is not null || owners.Count == 0)
                {
                    return new TelemetrySourceOwnerDecision(409, null, TelemetrySourceOwnership.SourceOwnedByAnotherProfileMessage(alias, foreign));
                }
            }

            var owner = existing is not null ? Clean(existing.OwnerProfileKey) : owners.FirstOrDefault();
            return new TelemetrySourceOwnerDecision(200, owner, null);
        }

        var historic = Clean(await catalog.GetLastKnownOwnerAsync(alias, ct).ConfigureAwait(false));
        if (historic is not null)
        {
            if (requested is not null && !string.Equals(requested, historic, StringComparison.OrdinalIgnoreCase))
            {
                return new TelemetrySourceOwnerDecision(409, null, TelemetrySourceOwnership.SourceOwnedByAnotherProfileMessage(alias, historic));
            }
            return new TelemetrySourceOwnerDecision(200, historic, null);
        }

        var newOwner = requested ?? await ActiveAsync(ct).ConfigureAwait(false);
        var known = await FindProfileAsync(newOwner, ct).ConfigureAwait(false);
        return known is null
            ? new TelemetrySourceOwnerDecision(400, null, TelemetrySourceOwnership.UnknownProfileMessage(newOwner))
            : new TelemetrySourceOwnerDecision(200, known, null);
    }

    /// <summary>
    /// A delete that names a profile (profileKey) must name the row's owner (409 otherwise). A delete
    /// that names nobody is allowed - the audit keeps the owner, so recreating the alias for another
    /// profile still needs an explicit transfer.
    /// </summary>
    public static TelemetrySourceOwnerDecision DecideDelete(string alias, TelemetrySourceEntry? existing, string? requestedProfileKey)
    {
        var requested = Clean(requestedProfileKey);
        var stored = Clean(existing?.OwnerProfileKey);
        return existing is not null && requested is not null && !string.Equals(requested, stored, StringComparison.OrdinalIgnoreCase)
            ? new TelemetrySourceOwnerDecision(409, stored, TelemetrySourceOwnership.SourceOwnedByAnotherProfileMessage(alias, stored))
            : new TelemetrySourceOwnerDecision(200, stored, null);
    }

    /// <summary>The stored key (its casing), or null when no such profile exists. Disabled profiles count: onboarding drafts are saved disabled.</summary>
    private async Task<string?> FindProfileAsync(string key, CancellationToken ct)
    {
        if (profiles is null)
        {
            if (string.Equals(key, KpiProfiles.Default, StringComparison.OrdinalIgnoreCase)) return KpiProfiles.Default;
            var active = await ActiveAsync(ct).ConfigureAwait(false);
            return string.Equals(key, active, StringComparison.OrdinalIgnoreCase) ? active : null;
        }

        return (await profiles.GetAllAsync(ct).ConfigureAwait(false))
            .Select(p => p.ProfileKey?.Trim())
            .FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<string> ActiveAsync(CancellationToken ct) =>
        activeProfile is null ? KpiProfiles.Default : await activeProfile.GetActiveAsync(ct).ConfigureAwait(false);

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}