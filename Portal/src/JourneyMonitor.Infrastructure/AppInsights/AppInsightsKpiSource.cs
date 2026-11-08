using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Configuration;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring.Kpi;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Core.UseCases;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Infrastructure.AppInsights;

/// <summary>
/// <see cref="IKpiSource"/> for <c>appinsights:latency</c> and <c>appinsights:exceptions</c> (contract v4 §2), and for
/// <c>appinsights:dependency</c> (contract v4 §5): entity grain only, one series per partner of the profile.
/// Loads the embedded KPI template, renders it with <see cref="CompanyKqlTemplater"/> for the
/// definition's <see cref="KpiDefinition.ProfileKey"/> and service (<see cref="KpiDefinition.Domain"/>),
/// replaces the remaining tokens with validated, formatted values (never free text), resolves
/// <c>{{SOURCE:alias}}</c> through <see cref="IKqlSourceResolver"/> and maps the rows into a
/// <see cref="KpiSourceResult"/>. The app id and tenant the query runs against come from the
/// telemetry source catalog for the service's first alias (contract v4 §5), with
/// <see cref="InsightsOptions"/> as the incumbent fallback (no alias, or an enabled row without
/// AppId/TenantId). An unknown
/// profile, a domain that is not a service of the profile or a service without a resolvable
/// query target throws <see cref="KpiSourceUnavailableException"/>.
/// </summary>
public sealed class AppInsightsKpiSource : IKpiSource
{
    public const string LatencySourceKey = "appinsights:latency";
    public const string ExceptionsSourceKey = "appinsights:exceptions";
    public const string DependencySourceKey = PartnerKpi.SourceKey;

    private const string ResourcePrefix = "JourneyMonitor.Infrastructure.Kql.";
    private const string TemplateSuffix = ".kql.template.txt";
    private const int MaxSamplesPerBucket = 100_000;
    private const int MaxOperationIdLength = 64;
    private const int MaxEntityKeyLength = 256;
    private const int MaxEntitiesLimit = 5000;

    private static readonly Regex TokenPattern = new(@"\{\{(\w+)\}\}", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex StagePattern = new(@"\A[A-Za-z0-9 _&-]{0,64}\z", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex DomainPattern = new(@"\A[A-Za-z0-9._-]{1,64}\z", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly ITelemetrySource _telemetry;
    private readonly IOptions<InsightsOptions> _insights;
    private readonly IOptions<JourneySyncOptions> _sync;
    private readonly ICompanyProfileStore _profiles;
    private readonly IKqlSourceResolver _sourceResolver;
    private readonly TelemetrySourceOwnershipCheck _ownership;
    private readonly ILogger<AppInsightsKpiSource> _logger;

    // Process-wide so the Warning is logged once per incomplete row, not once per scoped instance.
    private static readonly ConcurrentDictionary<string, byte> WarnedIncompleteRows = new(StringComparer.Ordinal);

    public AppInsightsKpiSource(
        ITelemetrySource telemetry,
        IOptions<InsightsOptions> insights,
        IOptions<JourneySyncOptions> sync,
        ICompanyProfileStore profiles,
        IKqlSourceResolver sourceResolver,
        ITelemetrySourceStore sourceStore,
        ILogger<AppInsightsKpiSource>? logger = null,
        TelemetrySourceOwnershipCheck? ownership = null)
    {
        _telemetry = telemetry;
        _insights = insights;
        _sync = sync;
        _profiles = profiles;
        _sourceResolver = sourceResolver;
        _ownership = ownership ?? new TelemetrySourceOwnershipCheck(sourceStore);
        _logger = logger ?? NullLogger<AppInsightsKpiSource>.Instance;
    }

    /// <summary>FX1: the only message a non-incumbent profile gets when the service has no usable catalog source.</summary>
    public static string NoSourceConfiguredMessage(string service, string profileKey) =>
        $"No telemetry source configured for service '{service}' of profile '{profileKey}'.";

    /// <summary><see cref="InsightsOptions"/> is the incumbent's configuration: no other profile may fall back to it.</summary>
    public static bool IsIncumbent(string? profileKey) =>
        string.Equals(profileKey?.Trim(), KpiProfiles.Default, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// FX1 read-time ownership: every alias in <paramref name="aliases"/> must be shared or owned by
    /// the definition's profile, otherwise the KPI is unavailable (a transferred alias stops being
    /// readable by its previous owner within <see cref="TelemetrySourceOwnershipCheck.CacheTtl"/>).
    /// </summary>
    public async Task EnsureAliasesReadableAsync(KpiDefinition definition, IEnumerable<string?> aliases, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(aliases);
        var label = $"{definition.Kpi}/{definition.Domain} [{definition.ProfileKey}]";
        IReadOnlyList<ForeignAliasOwner> foreign;
        try
        {
            foreign = await _ownership.FindForeignAsync(definition.ProfileKey, aliases, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new KpiSourceUnavailableException($"{label}: the telemetry source catalog could not be read: {ex.Message}");
        }
        if (foreign.Count > 0)
        {
            throw new KpiSourceUnavailableException($"{label}: " + string.Join(" ", foreign.Select(f => f.Message)));
        }
    }

    public bool CanHandle(string sourceKey) =>
        string.Equals(sourceKey, LatencySourceKey, StringComparison.OrdinalIgnoreCase)
        || string.Equals(sourceKey, ExceptionsSourceKey, StringComparison.OrdinalIgnoreCase)
        || string.Equals(sourceKey, DependencySourceKey, StringComparison.OrdinalIgnoreCase);

    public async Task<KpiSourceResult> FetchAsync(KpiDefinition definition, DateTimeOffset fromInclusive, DateTimeOffset toExclusive, int samplesPerBucket, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!CanHandle(definition.SourceKey))
        {
            throw new ArgumentException($"AppInsightsKpiSource does not handle source key '{definition.SourceKey}'.", nameof(definition));
        }
        EnsureSupportedShape(definition);

        var profile = await _profiles.GetAsync(definition.ProfileKey, ct)
            ?? throw new KpiSourceUnavailableException(
                $"{definition.Kpi}/{definition.Domain} [{definition.ProfileKey}]: no enabled company profile is stored under that key.");
        var service = profile.FindService(definition.Domain)
            ?? throw new KpiSourceUnavailableException(
                $"{definition.Kpi}/{definition.Domain} [{definition.ProfileKey}]: domain is not a service of the profile");
        var domain = service.Key.Trim().ToLowerInvariant();

        var target = await ResolveQueryTargetAsync(definition, service, ct);

        var template = LoadTemplate(definition.SourceKey, domain);
        var kql = RenderKql(template, profile, definition, fromInclusive, toExclusive, samplesPerBucket);

        try
        {
            kql = await _sourceResolver.ResolveAsync(kql, _sync.Value.Environment, ct);
        }
        catch (KqlSourceResolutionException ex)
        {
            throw new KpiSourceUnavailableException(
                $"{definition.Kpi}/{definition.Domain} [{definition.ProfileKey}]: {ex.Message}");
        }

        var rows = await _telemetry.QueryAsync(new TelemetryQueryRequest(kql, target.AppId, target.TenantId), ct);
        return MapRows(definition, rows);
    }

    /// <summary>
    /// The app id and tenant the query is executed against (contract v4 §5, source resolution):
    /// service → <see cref="ServiceDefinition.TelemetrySourceAliases"/> → <c>monitoring.TelemetrySource</c>
    /// row of the environment → its <c>AppId</c>/<c>TenantId</c>. The query itself already unions every
    /// alias of the service through <c>app("...")</c>, so the execution app is only the entry point:
    /// the FIRST alias of the service is the primary one, deterministically.
    /// <see cref="InsightsOptions"/> is the incumbent fallback (<see cref="KpiProfiles.Default"/> only), used when the service declares no
    /// alias, or when the primary row exists and is enabled but lacks its AppId or TenantId (a
    /// catalog not yet filled in must not silently drop every incumbent KPI; a one-time Warning names
    /// the alias). A missing or disabled primary row is unavailable, and so is a row without AppId
    /// when InsightsOptions does not configure the domain either. Any other profile without an alias,
    /// or whose primary row has no AppId, gets <see cref="NoSourceConfiguredMessage"/>. Every alias of
    /// the service must be readable by the profile (<see cref="EnsureAliasesReadableAsync"/>).
    /// </summary>
    public async Task<(string AppId, string? TenantId)> ResolveQueryTargetAsync(
        KpiDefinition definition, ServiceDefinition service, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(service);
        var domain = service.Key.Trim().ToLowerInvariant();
        var label = $"{definition.Kpi}/{definition.Domain} [{definition.ProfileKey}]";
        var incumbent = IsIncumbent(definition.ProfileKey);
        var aliases = (service.TelemetrySourceAliases ?? [])
            .Select(a => a?.Trim() ?? "")
            .Where(a => a.Length > 0)
            .ToList();
        var primaryAlias = aliases.FirstOrDefault();

        if (primaryAlias is null)
        {
            return incumbent
                ? ResolveFromInsightsOptions(label, domain)
                : throw new KpiSourceUnavailableException(NoSourceConfiguredMessage(service.Key.Trim(), definition.ProfileKey));
        }

        // The rendered query unions every alias of the service, so every one must be readable.
        await EnsureAliasesReadableAsync(definition, aliases, ct);

        string environment;
        try
        {
            environment = InsightsEnvironment.Normalize(_sync.Value.Environment);
        }
        catch (InsightsConfigurationException ex)
        {
            throw new KpiSourceUnavailableException($"{label}: {ex.Message}");
        }
        IReadOnlyList<TelemetrySourceEntry> sources;
        try
        {
            // Same rule as KqlSourceResolver: an empty table means the seed never ran.
            sources = await _ownership.GetCatalogAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new KpiSourceUnavailableException($"{label}: the telemetry source catalog could not be read: {ex.Message}");
        }

        var entry = sources.FirstOrDefault(s =>
            string.Equals(s.Alias?.Trim(), primaryAlias, StringComparison.OrdinalIgnoreCase)
            && string.Equals(s.Environment?.Trim(), environment, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            throw new KpiSourceUnavailableException(
                $"{label}: no telemetry source registered for alias \"{primaryAlias}\" in environment \"{environment}\".");
        }
        if (!entry.Enabled)
        {
            throw new KpiSourceUnavailableException(
                $"{label}: telemetry source \"{primaryAlias}\" is disabled for environment \"{environment}\".");
        }
        var rowAppId = string.IsNullOrWhiteSpace(entry.AppId) ? null : entry.AppId.Trim();
        var rowTenantId = string.IsNullOrWhiteSpace(entry.TenantId) ? null : entry.TenantId.Trim();
        if (rowAppId is not null && rowTenantId is not null)
        {
            return (rowAppId, rowTenantId);
        }

        if (!incumbent)
        {
            // FX1: InsightsOptions point at the incumbent's apps; another profile never reads through them.
            return rowAppId is not null
                ? (rowAppId, null)
                : throw new KpiSourceUnavailableException(NoSourceConfiguredMessage(service.Key.Trim(), definition.ProfileKey));
        }

        var fallback = TryResolveFromInsightsOptions(domain);
        if (fallback is not null)
        {
            var missing = rowAppId is null ? "AppId" : "TenantId";
            if (WarnedIncompleteRows.TryAdd($"{primaryAlias.ToLowerInvariant()}|{environment}|{missing}", 0))
            {
                _logger.LogWarning(
                    "[KpiSource] Telemetry source \"{Alias}\" ({Environment}) has no {Missing}; KPI queries for service \"{Domain}\" fall back to the Insights configuration until it is set on the source in Settings.",
                    primaryAlias, environment, missing, domain);
            }
            return fallback.Value;
        }
        if (rowAppId is null)
        {
            throw new KpiSourceUnavailableException(
                $"{label}: telemetry source \"{primaryAlias}\" ({environment}) has no AppId and App Insights is not configured for '{domain}'; set the Application Insights application id on the source.");
        }
        return (rowAppId, null);
    }

    private (string AppId, string? TenantId) ResolveFromInsightsOptions(string label, string domain) =>
        TryResolveFromInsightsOptions(domain)
        ?? throw new KpiSourceUnavailableException(
            $"{label}: the service has no telemetry source alias and App Insights is not configured for '{domain}'.");

    private (string AppId, string? TenantId)? TryResolveFromInsightsOptions(string domain)
    {
        InsightsSource source;
        try
        {
            source = InsightsSourceResolver.ResolveForDomain(_insights.Value, domain, _sync.Value.Environment);
        }
        catch (InsightsConfigurationException)
        {
            return null;
        }
        if (string.IsNullOrWhiteSpace(source.AppInsightsAppId)) return null;
        return (source.AppInsightsAppId.Trim(), string.IsNullOrWhiteSpace(source.TenantId) ? null : source.TenantId.Trim());
    }

    /// <summary>
    /// Embedded resource name of the KPI template for a source key and domain. A domain-specific
    /// <c>kpi-{kind}-{domain}.kql.template.txt</c> wins when one is embedded; otherwise the shared
    /// <c>kpi-{kind}.kql.template.txt</c>, which the profile alone specialises, is used.
    /// </summary>
    public static string ResourceName(string sourceKey, string domain)
    {
        var kind = KindOf(sourceKey);
        var key = (domain ?? "").Trim().ToLowerInvariant();
        if (DomainPattern.IsMatch(key))
        {
            var specific = $"{ResourcePrefix}kpi-{kind}-{key}{TemplateSuffix}";
            if (typeof(AppInsightsKpiSource).Assembly.GetManifestResourceInfo(specific) is not null) return specific;
        }
        return $"{ResourcePrefix}kpi-{kind}{TemplateSuffix}";
    }

    public static string LoadTemplate(string sourceKey, string domain)
    {
        var name = ResourceName(sourceKey, domain);
        using var stream = typeof(AppInsightsKpiSource).Assembly.GetManifestResourceStream(name)
            ?? throw new KpiSourceUnavailableException($"KPI KQL embedded resource not found: {name}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string KindOf(string sourceKey) =>
        string.Equals(sourceKey, LatencySourceKey, StringComparison.OrdinalIgnoreCase) ? "latency"
        : string.Equals(sourceKey, ExceptionsSourceKey, StringComparison.OrdinalIgnoreCase) ? "exceptions"
        : string.Equals(sourceKey, DependencySourceKey, StringComparison.OrdinalIgnoreCase) ? "dependency"
        : throw new ArgumentException($"Unknown App Insights KPI source key '{sourceKey}'.", nameof(sourceKey));

    /// <summary>
    /// Contract v4 §5: <c>appinsights:dependency</c> is entity grain only (EntityKey = partner key), and a
    /// dependency call carries no stage, so a domain-grain or staged definition is unavailable rather than
    /// silently answered with something else.
    /// </summary>
    public static void EnsureSupportedShape(KpiDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!string.Equals(definition.SourceKey?.Trim(), DependencySourceKey, StringComparison.OrdinalIgnoreCase)) return;

        if (definition.Grain != KpiGrain.Entity)
        {
            throw new KpiSourceUnavailableException(
                $"{definition.Kpi}/{definition.Domain} [{definition.ProfileKey}]: {DependencySourceKey} is entity grain only (one series per partner); set Grain to entity.");
        }
        if (!string.IsNullOrEmpty(definition.Stage))
        {
            throw new KpiSourceUnavailableException(
                $"{definition.Kpi}/{definition.Domain} [{definition.ProfileKey}]: {DependencySourceKey} has no stage (a dependency call is not classified into steps); leave Stage empty.");
        }
    }

    /// <summary>
    /// The whole KPI query for one definition, ready for <see cref="IKqlSourceResolver"/>: the
    /// profile placeholders are filled for the definition's service, every <c>{{token}}</c> is a
    /// validated literal, and only <c>{{SOURCE:alias}}</c> is left. A profile the templater rejects
    /// is <see cref="KpiSourceUnavailableException"/>: the KPI cannot be read for that profile.
    /// </summary>
    public static string RenderKql(string template, CompanyProfile profile, KpiDefinition definition, DateTimeOffset fromInclusive, DateTimeOffset toExclusive, int samplesPerBucket)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(definition);
        EnsureSupportedShape(definition);

        var service = profile.FindService(definition.Domain)
            ?? throw new KpiSourceUnavailableException(
                $"{definition.Kpi}/{definition.Domain} [{profile.ProfileKey}]: domain is not a service of the profile");

        string rendered;
        try
        {
            rendered = CompanyKqlTemplater.RenderForService(template, profile, service.Key);
        }
        catch (InvalidOperationException ex)
        {
            throw new KpiSourceUnavailableException(
                $"{definition.Kpi}/{definition.Domain} [{profile.ProfileKey}]: the profile cannot render the KPI query: {ex.Message}");
        }

        var kql = BuildKql(rendered, fromInclusive, toExclusive, definition.BucketSize, samplesPerBucket, definition.Stage,
            definition.Grain, service.Key, definition.MaxEntitiesPerBucket);
        KqlPlaceholders.AssertNoneLeft(kql, $"KPI query {definition.Kpi}/{definition.Domain} [{profile.ProfileKey}]");
        return kql;
    }

    /// <summary>
    /// Replaces every <c>{{token}}</c> with a validated literal. Throws <see cref="ArgumentException"/>
    /// for an invalid value and <see cref="InvalidOperationException"/> for an unknown token, so no
    /// placeholder or unvalidated text ever reaches App Insights. A value is only validated when its
    /// token is present.
    /// </summary>
    public static string BuildKql(string template, DateTimeOffset fromInclusive, DateTimeOffset toExclusive, TimeSpan bucketSize, int samplesPerBucket, string? stage,
        KpiGrain grain = KpiGrain.Domain, string? domain = null, int maxEntitiesPerBucket = 500)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (fromInclusive >= toExclusive) throw new ArgumentException("from must be earlier than to.", nameof(fromInclusive));

        var values = new Dictionary<string, Func<string>>(StringComparer.Ordinal)
        {
            ["from"] = () => FormatDateTime(fromInclusive, nameof(fromInclusive)),
            ["to"] = () => FormatDateTime(toExclusive, nameof(toExclusive)),
            ["bucket"] = () => FormatTimespan(bucketSize),
            ["perBucket"] = () => FormatPerBucket(samplesPerBucket),
            ["stage"] = () => FormatStage(stage),
            ["grain"] = () => FormatGrain(grain),
            ["domain"] = () => FormatDomain(domain),
            ["maxEntities"] = () => FormatMaxEntities(maxEntitiesPerBucket),
        };

        var formatted = new Dictionary<string, string>(StringComparer.Ordinal);
        return TokenPattern.Replace(template, match =>
        {
            var name = match.Groups[1].Value;
            if (formatted.TryGetValue(name, out var cached)) return cached;
            if (!values.TryGetValue(name, out var format))
            {
                throw new InvalidOperationException($"Unknown KQL token '{match.Value}'.");
            }
            return formatted[name] = format();
        });
    }

    public static string FormatDateTime(DateTimeOffset value, string paramName = "value")
    {
        if (value.UtcTicks % TimeSpan.TicksPerSecond != 0)
        {
            throw new ArgumentException("KQL datetime must be a whole second.", paramName);
        }
        return "datetime(" + value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) + ")";
    }

    public static string FormatTimespan(TimeSpan value)
    {
        if (value < TimeSpan.FromSeconds(1) || value.Ticks % TimeSpan.TicksPerSecond != 0)
        {
            throw new ArgumentException("Bucket size must be a positive whole number of seconds.", nameof(value));
        }
        var inv = CultureInfo.InvariantCulture;
        if (value.Ticks % TimeSpan.TicksPerDay == 0) return (value.Ticks / TimeSpan.TicksPerDay).ToString(inv) + "d";
        if (value.Ticks % TimeSpan.TicksPerHour == 0) return (value.Ticks / TimeSpan.TicksPerHour).ToString(inv) + "h";
        if (value.Ticks % TimeSpan.TicksPerMinute == 0) return (value.Ticks / TimeSpan.TicksPerMinute).ToString(inv) + "m";
        return (value.Ticks / TimeSpan.TicksPerSecond).ToString(inv) + "s";
    }

    public static string FormatPerBucket(int value)
    {
        if (value is < 0 or > MaxSamplesPerBucket)
        {
            throw new ArgumentOutOfRangeException(nameof(value), $"samplesPerBucket must be 0..{MaxSamplesPerBucket}.");
        }
        return value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Only <c>[A-Za-z0-9 _&amp;-]{0,64}</c> is accepted (no quote, backslash or newline), so the quoted literal needs no escaping.</summary>
    public static string FormatStage(string? stage)
    {
        var value = stage ?? "";
        if (!StagePattern.IsMatch(value))
        {
            throw new ArgumentException("Stage may only contain letters, digits, space, '_', '&' or '-' (max 64).", nameof(stage));
        }
        return "\"" + value + "\"";
    }

    /// <summary>The bare word <c>domain</c> or <c>entity</c>: an enum name, so it cannot carry text, and a template may use it as an identifier part.</summary>
    public static string FormatGrain(KpiGrain grain) => grain switch
    {
        KpiGrain.Domain => "domain",
        KpiGrain.Entity => "entity",
        _ => throw new ArgumentOutOfRangeException(nameof(grain), $"Unknown KPI grain '{grain}'."),
    };

    /// <summary>The service key as a KQL string literal; the key comes from the profile, and <see cref="CompanyKqlTemplater.Literal"/> escapes it.</summary>
    public static string FormatDomain(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
        {
            throw new ArgumentException("A KPI template that filters by service needs the service key.", nameof(domain));
        }
        return CompanyKqlTemplater.Literal(domain.Trim());
    }

    public static string FormatMaxEntities(int value)
    {
        if (value is < 1 or > MaxEntitiesLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(value), $"MaxEntitiesPerBucket must be 1..{MaxEntitiesLimit}.");
        }
        return value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Maps the rows for the definition's grain. Domain grain is the v1 contract (buckets and
    /// samples, entity columns ignored). Entity grain keeps only bucket rows that name an entity,
    /// carries P50/P95/Mean/FailedCount, drops samples, and keeps at most
    /// <see cref="KpiDefinition.MaxEntitiesPerBucket"/> series per bucket, highest traffic first.
    /// </summary>
    public static KpiSourceResult MapRows(KpiDefinition definition, IReadOnlyList<TelemetryJourneyRow> rows)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return definition.Grain == KpiGrain.Entity
            ? MapEntityRows(definition.MaxEntitiesPerBucket, rows)
            : MapRows(definition.KpiDefinitionId, rows);
    }

    private static KpiSourceResult MapEntityRows(int maxEntitiesPerBucket, IReadOnlyList<TelemetryJourneyRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var cap = Math.Clamp(maxEntitiesPerBucket, 1, MaxEntitiesLimit);
        var series = new List<KpiSourceBucket>();

        foreach (var row in rows)
        {
            if (!string.Equals(row.GetString("kind"), "bucket", StringComparison.OrdinalIgnoreCase)) continue;
            var bucketStart = ReadDateTime(row, "BucketStart");
            if (bucketStart is null) continue;

            var entityKey = row.GetString("EntityKey").Trim();
            // Truncating could merge two entities into one series; a key that does not fit is dropped.
            if (entityKey.Length == 0 || entityKey.Length > MaxEntityKeyLength) continue;

            var failed = row.GetDouble("FailedCount");
            series.Add(new KpiSourceBucket(bucketStart.Value, (long)Math.Round(row.GetDouble("TotalCount") ?? 0), row.GetDouble("Value"))
            {
                EntityKey = entityKey,
                P50 = row.GetDouble("P50"),
                P95 = row.GetDouble("P95"),
                Mean = row.GetDouble("Mean"),
                FailedCount = failed is null ? null : (long)Math.Round(failed.Value),
            });
        }

        var buckets = series
            .GroupBy(b => (b.BucketStart, b.EntityKey))
            .Select(g => g.First())
            .GroupBy(b => b.BucketStart)
            .OrderBy(g => g.Key)
            .SelectMany(g => g
                .OrderByDescending(b => b.TotalCount)
                .ThenBy(b => b.EntityKey, StringComparer.Ordinal)
                .Take(cap))
            .ToList();

        return new KpiSourceResult(buckets, []);
    }

    public static KpiSourceResult MapRows(int kpiDefinitionId, IReadOnlyList<TelemetryJourneyRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var buckets = new List<KpiSourceBucket>();
        var samples = new List<KpiSample>();

        foreach (var row in rows)
        {
            var bucketStart = ReadDateTime(row, "BucketStart");
            if (bucketStart is null) continue;

            var kind = row.GetString("kind");
            if (string.Equals(kind, "bucket", StringComparison.OrdinalIgnoreCase))
            {
                var total = row.GetDouble("TotalCount") ?? 0;
                buckets.Add(new KpiSourceBucket(bucketStart.Value, (long)Math.Round(total), row.GetDouble("Value")));
            }
            else if (string.Equals(kind, "sample", StringComparison.OrdinalIgnoreCase))
            {
                var value = row.GetDouble("Value");
                if (value is null) continue;
                var operationId = row.GetString("OperationId");
                if (operationId.Length > MaxOperationIdLength) operationId = operationId[..MaxOperationIdLength];
                samples.Add(new KpiSample(
                    0,
                    kpiDefinitionId,
                    bucketStart.Value,
                    ReadDateTime(row, "Timestamp") ?? bucketStart.Value,
                    value.Value,
                    string.IsNullOrEmpty(operationId) ? null : operationId));
            }
        }

        return new KpiSourceResult(buckets, samples);
    }

    /// <summary>Kusto returns datetimes as ISO-8601 UTC strings; a value without an offset is read as UTC.</summary>
    private static DateTimeOffset? ReadDateTime(TelemetryJourneyRow row, string column)
    {
        var text = row.GetString(column);
        if (string.IsNullOrWhiteSpace(text)) return null;
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed.ToUniversalTime()
            : null;
    }
}
