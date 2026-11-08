using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DemoGenerator.Archetypes;

namespace DemoGenerator.Packs;

/// <summary>
/// Checks a company pack (<c>Portal/demo/packs/&lt;companyKey&gt;/</c>, contract v5 §6) before it is seeded.
/// <see cref="Validate"/> returns every problem as "<c>file: message</c>"; empty means the pack is consistent
/// with itself AND will be accepted by the platform APIs it is seeded through. The platform rules are
/// re-implemented here from the platform's own validators (no project reference: the demo is independent).
/// </summary>
public static partial class PackValidator
{
    public const string RootCauseMarker = "Root Cause Analysis";
    public const int MinCorpusDocuments = 10;
    public const int MinBusinessKqlKpis = 2;

    public static readonly TimeSpan RequiredBucket = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan RequiredFrequency = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan MinLookback = TimeSpan.FromDays(1);

    /// <summary>Entity-grain retention of the platform (KpiEntityRetention.Retention).</summary>
    public static readonly TimeSpan EntityRetention = TimeSpan.FromDays(8);

    /// <summary>monitoring.KpiDefinition.Domain is nvarchar(32).</summary>
    public const int MaxDomainLength = 32;

    public static readonly IReadOnlyList<string> StepMatcherKinds =
        ["operation-contains", "operation-equals", "page-name-contains", "path-contains", "regex", "url-contains"];

    public static readonly IReadOnlyList<string> PartnerMatcherKinds =
        ["dependency-target-contains", "dependency-name-contains", "dependency-type-equals", "regex"];

    public static readonly IReadOnlyList<string> IdentityKinds = ["custom-dimension", "query-parameter", "host", "role-name"];

    public static readonly IReadOnlyList<string> AppInsightsSourceKeys = ["appinsights:latency", "appinsights:exceptions", "appinsights:dependency"];

    public static readonly IReadOnlyList<string> KqlAllowedTokens = ["from", "to", "bucket", "perBucket", "stage", "grain"];

    public static readonly IReadOnlyList<string> CrossResourceFunctions =
    [
        "app", "workspace", "cluster", "database", "resource", "adx", "external_table", "externaldata",
        "http_request", "http_request_post", "sql_request", "cosmosdb_sql_request", "mysql_request",
        "postgresql_request", "azure_digital_twins_query_request",
    ];

    /// <summary>InsightsSourceResolver's resource id rule: what PUT /api/settings/telemetry-sources accepts.</summary>
    [GeneratedRegex(@"^/subscriptions/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/resourceGroups/[A-Za-z0-9._()-]+/providers/[Mm]icrosoft\.[Ii]nsights/components/(?<name>[A-Za-z0-9._-]+)$")]
    private static partial Regex ResourceIdPattern();

    [GeneratedRegex(@"^[a-z0-9._-]{1,64}$")]
    private static partial Regex AliasPattern();

    [GeneratedRegex(@"\Akpi:[a-z0-9:._-]+\z")]
    private static partial Regex CustomKpiQueryKey();

    [GeneratedRegex(@"\{\{(?<name>[^{}\r\n]{0,128})\}\}")]
    private static partial Regex TokenPattern();

    [GeneratedRegex(@"\{\{SOURCE:(?<alias>[A-Za-z0-9._-]{1,64})\}\}")]
    private static partial Regex SourcePlaceholder();

    [GeneratedRegex(@"(?<![\w])app\s*\(\s*(?<q>['""])\{\{SOURCE:[A-Za-z0-9._-]{1,64}\}\}\k<q>\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex AllowedSourceCall();

    private static readonly JsonSerializerOptions Json = ArchetypeLoader.JsonOptions;

    public static IReadOnlyList<string> Validate(string packDir)
    {
        var errors = new List<string>();
        if (!Directory.Exists(packDir)) return [$"Pack folder '{packDir}' does not exist."];

        var (archetype, archetypeErrors) = ArchetypeLoader.TryLoad(Path.Combine(packDir, "archetype.json"));
        errors.AddRange(archetypeErrors.Select(e => "archetype.json: " + e));
        var profile = Read<ProfileDocument>(packDir, "profile.json", errors);
        var sources = Read<List<SourceDocument?>>(packDir, "sources.json", errors);
        var kpis = Read<List<KpiDocument?>>(packDir, "kpis.json", errors);

        if (profile is not null) ValidateProfile(profile, errors);
        if (sources is not null) ValidateSourceRows(sources, archetype, errors);
        if (kpis is not null) ValidateKpiRows(kpis, archetype, profile, errors);

        if (archetype is not null && profile is not null) ValidateProfileAgainstArchetype(archetype, profile, errors);
        if (archetype is not null && sources is not null) ValidateSourcesAgainstArchetype(archetype, sources, errors);
        if (archetype is not null && profile is not null && sources is not null) ValidateCoverage(archetype, profile, sources, errors);
        if (profile is not null && sources is not null) ValidateAliases(profile, sources, errors);
        if (archetype is not null && profile is not null && kpis is not null) ValidateKpiCoverage(archetype, profile, kpis, errors);

        ValidateCorpus(packDir, errors);
        var readme = Path.Combine(packDir, "README.md");
        if (!File.Exists(readme) || string.IsNullOrWhiteSpace(File.ReadAllText(readme)))
            errors.Add("README.md: missing or empty; write the demo script of this company (scenario to trigger, what to look at).");
        return errors;
    }

    private static T? Read<T>(string packDir, string file, List<string> errors) where T : class
    {
        var path = Path.Combine(packDir, file);
        if (!File.Exists(path))
        {
            errors.Add($"{file}: missing.");
            return null;
        }
        try
        {
            var value = JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json);
            if (value is null) errors.Add($"{file}: is null.");
            return value;
        }
        catch (JsonException ex)
        {
            errors.Add($"{file}: not valid for its shape ({ex.Message}).");
            return null;
        }
    }

    // ------------------------------------------------------------------ profile.json (CompanyProfileValidator errors)

    private static void ValidateProfile(ProfileDocument p, List<string> errors)
    {
        void E(string m) => errors.Add("profile.json: " + m);

        if (string.IsNullOrWhiteSpace(p.ProfileKey)) E("$.profileKey is required.");
        if (string.IsNullOrWhiteSpace(p.DisplayName)) E("$.displayName is required.");
        if (!p.Enabled) E("$.enabled must be true: a disabled profile cannot be selected in the demo.");
        if (p.Labels is null) E("$.labels is required.");
        else
        {
            foreach (var (name, value) in new[] { ("service", p.Labels.Service), ("flow", p.Labels.Flow), ("step", p.Labels.Step), ("entity", p.Labels.Entity), ("owner", p.Labels.Owner) })
                if (string.IsNullOrWhiteSpace(value)) E($"$.labels.{name} is required.");
        }
        if (p.Glossary is null) E("$.glossary is required.");
        else
        {
            foreach (var (name, value) in new[] { ("serviceNoun", p.Glossary.ServiceNoun), ("flowNoun", p.Glossary.FlowNoun), ("stepNoun", p.Glossary.StepNoun), ("entityNoun", p.Glossary.EntityNoun), ("ownerNoun", p.Glossary.OwnerNoun) })
                if (string.IsNullOrWhiteSpace(value)) E($"$.glossary.{name} is required.");
        }

        var services = p.Services ?? [];
        if (services.Count == 0) E("$.services must declare at least one service.");
        var serviceKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fallbacks = 0;
        for (var s = 0; s < services.Count; s++)
        {
            var service = services[s];
            var sp = $"$.services[{s}]";
            if (service is null) { E($"{sp} is null."); continue; }
            if (string.IsNullOrWhiteSpace(service.Key)) E($"{sp}.key is required.");
            else if (!serviceKeys.Add(service.Key.Trim())) E($"{sp}.key '{service.Key}' is repeated (keys are case-insensitive).");
            var flows = service.Flows ?? [];
            if (flows.Count == 0) { E($"{sp}.flows must declare at least one flow."); continue; }
            var flowKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var stepKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var f = 0; f < flows.Count; f++)
            {
                var flow = flows[f];
                var fp = $"{sp}.flows[{f}]";
                if (flow is null) { E($"{fp} is null."); continue; }
                if (string.IsNullOrWhiteSpace(flow.Key)) E($"{fp}.key is required.");
                else if (!flowKeys.Add(flow.Key.Trim())) E($"{fp}.key '{flow.Key}' is repeated in this service.");
                var steps = flow.Steps ?? [];
                if (steps.Count == 0) { E($"{fp}.steps must declare at least one step."); continue; }
                for (var t = 0; t < steps.Count; t++)
                {
                    var step = steps[t];
                    var tp = $"{fp}.steps[{t}]";
                    if (step is null) { E($"{tp} is null."); continue; }
                    if (string.IsNullOrWhiteSpace(step.Key)) E($"{tp}.key is required.");
                    else if (!stepKeys.Add(step.Key.Trim())) E($"{tp}.key '{step.Key}' is repeated in this service.");
                    var matchers = step.Matchers ?? [];
                    if (matchers.Count == 0) E($"{tp}.matchers must have at least one matcher.");
                    for (var m = 0; m < matchers.Count; m++)
                    {
                        var matcher = matchers[m];
                        var mp = $"{tp}.matchers[{m}]";
                        if (matcher is null) { E($"{mp} is null."); continue; }
                        if (!StepMatcherKinds.Contains(matcher.Kind?.Trim() ?? "", StringComparer.OrdinalIgnoreCase))
                            E($"{mp}.kind '{matcher.Kind}' is unknown. Use one of: {string.Join(", ", StepMatcherKinds)}.");
                        if (string.IsNullOrWhiteSpace(matcher.Value)) E($"{mp}.value is required.");
                        else if (string.Equals(matcher.Kind?.Trim(), "regex", StringComparison.OrdinalIgnoreCase) && !Compiles(matcher.Value, !matcher.CaseSensitive))
                            E($"{mp}.value regex '{matcher.Value}' does not compile.");
                    }
                }
            }
            if (!string.IsNullOrWhiteSpace(service.FallbackStep))
            {
                fallbacks++;
                if (!stepKeys.Contains(service.FallbackStep.Trim())) E($"{sp}.fallbackStep '{service.FallbackStep}' is not a step key of this service.");
            }
        }
        if (fallbacks > 1) E("$.services: at most one service may declare a fallbackStep.");

        var identities = p.EntityIdentitySources ?? [];
        if (identities.Count == 0) E("$.entityIdentitySources must declare at least one source.");
        for (var i = 0; i < identities.Count; i++)
        {
            var source = identities[i];
            var ip = $"$.entityIdentitySources[{i}]";
            if (source is null) { E($"{ip} is null."); continue; }
            var kind = source.Kind?.Trim() ?? "";
            if (!IdentityKinds.Contains(kind, StringComparer.OrdinalIgnoreCase)) E($"{ip}.kind '{source.Kind}' is unknown. Use one of: {string.Join(", ", IdentityKinds)}.");
            else if (kind is "custom-dimension" or "query-parameter" && string.IsNullOrWhiteSpace(source.Key)) E($"{ip}.key is required for kind '{kind}'.");
            if (string.IsNullOrWhiteSpace(source.EntityKind)) E($"{ip}.entityKind is required.");
        }

        var partnerKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var partners = p.Partners ?? [];
        for (var i = 0; i < partners.Count; i++)
        {
            var partner = partners[i];
            var pp = $"$.partners[{i}]";
            if (partner is null) { E($"{pp} is null."); continue; }
            var key = partner.Key?.Trim() ?? "";
            if (key.Length is 0 or > 64) E($"{pp}.key must be 1-64 characters.");
            else if (key.Contains("{{", StringComparison.Ordinal) || key.Contains("}}", StringComparison.Ordinal)) E($"{pp}.key must not contain '{{{{' or '}}}}'.");
            else if (!partnerKeys.Add(key)) E($"{pp}.key '{key}' is repeated (keys are case-insensitive).");
            var matchers = partner.Matchers ?? [];
            if (matchers.Count == 0) E($"{pp}.matchers must have at least one matcher.");
            for (var m = 0; m < matchers.Count; m++)
            {
                var matcher = matchers[m];
                var mp = $"{pp}.matchers[{m}]";
                if (matcher is null) { E($"{mp} is null."); continue; }
                if (!PartnerMatcherKinds.Contains(matcher.Kind?.Trim() ?? "", StringComparer.OrdinalIgnoreCase))
                    E($"{mp}.kind '{matcher.Kind}' is unknown. Use one of: {string.Join(", ", PartnerMatcherKinds)}.");
                if (string.IsNullOrWhiteSpace(matcher.Value)) E($"{mp}.value is required.");
                else if (matcher.Value.Contains("{{", StringComparison.Ordinal) || matcher.Value.Contains("}}", StringComparison.Ordinal)) E($"{mp}.value must not contain '{{{{' or '}}}}'.");
                else if (string.Equals(matcher.Kind?.Trim(), "regex", StringComparison.OrdinalIgnoreCase))
                {
                    // Same order as CompanyProfileValidator.ValidatePartnerMatcher: compile, empty match, then RE2 support.
                    if (!Compiles(matcher.Value, true) || new Regex(matcher.Value, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)).IsMatch(""))
                        E($"{mp}.value regex '{matcher.Value}' does not compile or matches the empty string.");
                    else if (FindUnsupportedRe2Construct(matcher.Value) is { } construct)
                        E($"{mp}.value regex '{matcher.Value}' uses {construct}, which KQL (RE2) does not support; the platform rejects the profile (partner-regex-unsupported).");
                }
            }
        }

        if (p.Privacy is { } privacy)
        {
            foreach (var (dim, i) in (privacy.PiiDimensions ?? []).Select((d, i) => (d, i)))
                if (string.IsNullOrWhiteSpace(dim)) E($"$.privacy.piiDimensions[{i}] is blank.");
            foreach (var (pattern, i) in (privacy.Patterns ?? []).Select((x, i) => (x, i)))
                if (string.IsNullOrWhiteSpace(pattern) || !Compiles(pattern, false) || new Regex(pattern, RegexOptions.None, TimeSpan.FromMilliseconds(100)).IsMatch(""))
                    E($"$.privacy.patterns[{i}] '{pattern}' must compile and must not match the empty string.");
        }
    }

    private static void ValidateProfileAgainstArchetype(Archetype a, ProfileDocument p, List<string> errors)
    {
        if (!string.Equals(p.ProfileKey, a.ProfileKey, StringComparison.Ordinal))
            errors.Add($"profile.json: $.profileKey '{p.ProfileKey}' must equal the archetype profileKey '{a.ProfileKey}'.");
        if (!string.Equals(p.DisplayName?.Trim(), a.DisplayName?.Trim(), StringComparison.Ordinal))
            errors.Add($"profile.json: $.displayName '{p.DisplayName}' must equal the archetype displayName '{a.DisplayName}'.");

        var dimension = a.Entities?.Dimension ?? "";
        if (!(p.EntityIdentitySources ?? []).Any(s => s is not null
                && string.Equals(s.Kind?.Trim(), "custom-dimension", StringComparison.OrdinalIgnoreCase)
                && string.Equals(s.Key?.Trim(), dimension, StringComparison.OrdinalIgnoreCase)))
            errors.Add($"profile.json: $.entityIdentitySources has no custom-dimension source with key '{dimension}' (the archetype entities.dimension), so no alert can name the affected {a.Entities?.Kind}.");

        var partners = (p.Partners ?? []).Where(x => x is not null && !string.IsNullOrWhiteSpace(x.Key)).Select(x => x!).ToList();
        foreach (var step in a.Steps ?? [])
        {
            foreach (var dep in step.Dependencies ?? [])
            {
                var declared = partners.FirstOrDefault(x => string.Equals(x.Key!.Trim(), dep.Partner, StringComparison.OrdinalIgnoreCase));
                if (declared is null)
                {
                    errors.Add($"profile.json: partner '{dep.Partner}' (dependency '{dep.Name}' of step '{step.Name}') is not declared in $.partners.");
                    continue;
                }
                // The platform classifies a dependency with the FIRST partner whose matchers hit it.
                var claimedBy = partners.FirstOrDefault(x => (x.Matchers ?? []).Any(m => m is not null && PartnerMatches(m, dep)));
                if (claimedBy is null)
                    errors.Add($"profile.json: no matcher of partner '{dep.Partner}' matches dependency target '{dep.Target}' / name '{dep.Name}' / type '{dep.Type}' (step '{step.Name}').");
                else if (!ReferenceEquals(claimedBy, declared))
                    errors.Add($"profile.json: dependency '{dep.Name}' to '{dep.Target}' belongs to partner '{dep.Partner}' but partner '{claimedBy.Key}' is declared earlier and claims it first.");
            }
        }
    }

    // ------------------------------------------------------------------ sources.json

    private static void ValidateSourceRows(List<SourceDocument?> sources, Archetype? a, List<string> errors)
    {
        void E(string m) => errors.Add("sources.json: " + m);
        if (sources.Count == 0) E("must list one source per archetype app.");
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < sources.Count; i++)
        {
            var s = sources[i];
            var p = $"[{i}]";
            if (s is null) { E($"{p} is null."); continue; }
            if (string.IsNullOrWhiteSpace(s.Alias) || !AliasPattern().IsMatch(s.Alias)) E($"{p}.alias '{s.Alias}' must be 1-64 lowercase letters, digits, '.', '-' or '_'.");
            else if (!aliases.Add(s.Alias)) E($"{p}.alias '{s.Alias}' is repeated.");
            else if (a?.CompanyKey is { } ck && ck != "tourism" && !s.Alias.StartsWith(ck + "-", StringComparison.Ordinal))
                E($"{p}.alias '{s.Alias}' must be '<companyKey>-<role>' ('{ck}-…'); only tourism reuses the real Arrivia aliases.");
            if (!string.Equals(s.Environment, "production", StringComparison.Ordinal)) E($"{p}.environment must be \"production\".");
            if (s.Kind is not null && !string.Equals(s.Kind, "app-insights", StringComparison.Ordinal)) E($"{p}.kind must be \"app-insights\" or omitted.");
            if (string.IsNullOrWhiteSpace(s.ResourceId) || !ResourceIdPattern().IsMatch(s.ResourceId))
                E($"{p}.resourceId '{s.ResourceId}' is not accepted by PUT /api/settings/telemetry-sources: it must be /subscriptions/<hex guid>/resourceGroups/<rg>/providers/microsoft.insights/components/<database> (the subscription id must be hexadecimal, e.g. 00000000-0000-0000-0000-000000000000).");
            if (string.IsNullOrWhiteSpace(s.AppId)) E($"{p}.appId is required (the archetype appId the fake App Insights answers to).");
            if (a is not null && !string.Equals(s.OwnerProfileKey, a.ProfileKey, StringComparison.Ordinal))
                E($"{p}.ownerProfileKey '{s.OwnerProfileKey}' must be the archetype profileKey '{a.ProfileKey}'.");
            if (s.Enabled != true) E($"{p}.enabled must be true.");
        }
    }

    private static void ValidateSourcesAgainstArchetype(Archetype a, List<SourceDocument?> sources, List<string> errors)
    {
        var rows = sources.Where(s => s is not null).Select(s => s!).ToList();
        foreach (var app in a.Apps ?? [])
        {
            var matching = rows.Where(s => string.Equals(s.AppId, app.AppId, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matching.Count == 0)
            {
                errors.Add($"sources.json: archetype app '{app.AppId}' (database '{app.Database}') has no source; add {{ \"alias\": …, \"appId\": \"{app.AppId}\", \"resourceId\": \"…/components/{app.Database}\" }}.");
                continue;
            }
            if (matching.Count > 1) errors.Add($"sources.json: archetype app '{app.AppId}' has {matching.Count} sources; keep one.");
            foreach (var source in matching)
            {
                var name = source.ResourceId is null ? null : ResourceIdPattern().Match(source.ResourceId) is { Success: true } m ? m.Groups["name"].Value : null;
                if (name is not null && !string.Equals(name, app.Database, StringComparison.OrdinalIgnoreCase))
                    errors.Add($"sources.json: source '{source.Alias}' points at component '{name}' but app '{app.AppId}' lives in database '{app.Database}'; end the resourceId with /components/{app.Database}.");
            }
        }
        foreach (var source in rows.Where(s => !(a.Apps ?? []).Any(x => string.Equals(x.AppId, s.AppId, StringComparison.OrdinalIgnoreCase))))
            errors.Add($"sources.json: source '{source.Alias}' has appId '{source.AppId}', which is not an archetype app.");
    }

    private static void ValidateAliases(ProfileDocument p, List<SourceDocument?> sources, List<string> errors)
    {
        var known = sources.Where(s => s?.Alias is not null).Select(s => s!.Alias!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var service in (p.Services ?? []).Where(s => s is not null))
        {
            var aliases = service!.TelemetrySourceAliases ?? [];
            used.UnionWith(aliases.Where(x => !string.IsNullOrWhiteSpace(x)));
            if (!aliases.Any(known.Contains))
                errors.Add($"profile.json: service '{service.Key}' lists no alias of sources.json in telemetrySourceAliases ({string.Join(", ", aliases)}), so none of its queries can reach the demo data.");
        }
        foreach (var alias in known.Where(x => !used.Contains(x)))
            errors.Add($"sources.json: alias '{alias}' is not in the telemetrySourceAliases of any profile service; its telemetry would never be read.");
    }

    // ------------------------------------------------------------------ coverage: every archetype step resolves to a profile step

    private static void ValidateCoverage(Archetype a, ProfileDocument p, List<SourceDocument?> sources, List<string> errors)
    {
        var entityIds = ArchetypeLoader.EntityIds(a.Entities);
        foreach (var step in a.Steps ?? [])
        {
            var app = (a.Apps ?? []).FirstOrDefault(x => string.Equals(x.AppId, step.App, StringComparison.OrdinalIgnoreCase));
            if (app is null) continue;
            var host = a.Entities?.HostTemplate is { } template && entityIds.Count > 0
                ? template.Replace("{0}", entityIds[0], StringComparison.Ordinal)
                : app.Host ?? "";
            var url = "https://" + host + step.Url;
            var aliases = sources.Where(s => s is not null && string.Equals(s.AppId, app.AppId, StringComparison.OrdinalIgnoreCase) && s.Alias is not null)
                .Select(s => s!.Alias!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var services = (p.Services ?? []).Where(s => s is not null && (s.TelemetrySourceAliases ?? []).Any(aliases.Contains)).Select(s => s!).ToList();

            var covered = services.Any(service =>
                HostAccepted(service, host)
                && (service.Flows ?? []).Any(f => (f?.Steps ?? []).Any(t => (t?.Matchers ?? []).Any(m => m is not null && StepMatches(m, url, step.Name ?? "")))));
            if (!covered)
            {
                var where = services.Count == 0
                    ? $"no profile service lists the alias of app '{app.AppId}' ({string.Join(", ", aliases)})"
                    : $"no step matcher of service(s) {string.Join(", ", services.Select(s => s.Key))} matches it (or their hostFilters reject '{host}')";
                errors.Add($"profile.json: archetype step '{step.Name}' (url {step.Url}, app {app.AppId}) is not covered: {where}. Add a matcher such as path-contains \"{(step.Url ?? "").Split('?')[0].ToLowerInvariant()}\".");
            }
        }
    }

    public static bool HostAccepted(ProfileService service, string host)
    {
        var terms = (service.HostFilters ?? []).Where(h => !string.IsNullOrWhiteSpace(h)).Select(h => h.Trim().TrimStart('*', '.')).Where(h => h.Length > 0).ToList();
        if ((service.HostFilters ?? []).Count == 0) return true;
        return terms.Any(t => string.Equals(host, t, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + t, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The platform's KQL for each step matcher kind (CompanyKqlTemplater), evaluated on one request.</summary>
    public static bool StepMatches(ProfileStepMatcher matcher, string url, string operationName)
    {
        var value = matcher.Value ?? "";
        var comparison = matcher.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        switch (matcher.Kind?.Trim().ToLowerInvariant())
        {
            case "url-contains":
                return url.Contains(value, comparison);
            case "path-contains":
                var path = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url;
                return path.ToLowerInvariant().Contains(value, comparison);
            case "operation-equals":
                return string.Equals(operationName.Trim(), value.Trim(), comparison);
            case "operation-contains":
                return operationName.Contains(value, comparison);
            case "regex":
                try
                {
                    var options = matcher.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
                    return Regex.IsMatch(url, value, options, TimeSpan.FromMilliseconds(100)) || Regex.IsMatch(operationName, value, options, TimeSpan.FromMilliseconds(100));
                }
                catch (ArgumentException)
                {
                    return false;
                }
            default:
                // page-name-contains tests PageName, which a request does not carry.
                return false;
        }
    }

    public static bool PartnerMatches(ProfilePartnerMatcher matcher, DependencyModel dependency)
    {
        var value = matcher.Value ?? "";
        switch (matcher.Kind?.Trim().ToLowerInvariant())
        {
            case "dependency-target-contains": return (dependency.Target ?? "").Contains(value, StringComparison.OrdinalIgnoreCase);
            case "dependency-name-contains": return (dependency.Name ?? "").Contains(value, StringComparison.OrdinalIgnoreCase);
            case "dependency-type-equals": return string.Equals((dependency.Type ?? "").Trim(), value.Trim(), StringComparison.OrdinalIgnoreCase);
            case "regex":
                try
                {
                    return Regex.IsMatch(dependency.Target ?? "", value, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100))
                        || Regex.IsMatch(dependency.Name ?? "", value, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
                }
                catch (ArgumentException)
                {
                    return false;
                }
            default: return false;
        }
    }

    // ------------------------------------------------------------------ kpis.json (KpiDefinition.Validate + POST /api/settings/kpis + demo rules)

    private static void ValidateKpiRows(List<KpiDocument?> kpis, Archetype? a, ProfileDocument? p, List<string> errors)
    {
        var serviceKeys = (p?.Services ?? []).Where(s => s?.Key is not null).Select(s => s!.Key!.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var aliases = (p?.Services ?? []).Where(s => s is not null).SelectMany(s => s!.TelemetrySourceAliases ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var naturalKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queryKeys = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < kpis.Count; i++)
        {
            var k = kpis[i];
            var label = k is null ? $"[{i}]" : $"[{i}] {k.Pillar}/{k.Kpi}/{k.Domain}/{k.Grain ?? "domain"}";
            void E(string m) => errors.Add($"kpis.json: {label}: {m}");
            if (k is null) { E("is null."); continue; }

            if ((k.KpiDefinitionId ?? 0) != 0) E("kpiDefinitionId must be 0 (the seed creates the KPI).");
            if (!TryPillar(k.Pillar, out var pillar)) E("pillar must be Business, Application or Infrastructure.");
            if (string.IsNullOrWhiteSpace(k.Kpi) || k.Kpi.Trim().Length > 64) E("kpi is required (max 64).");
            if (string.IsNullOrWhiteSpace(k.Domain)) E("domain is required.");
            else if (k.Domain.Trim().Length > MaxDomainLength) E($"domain must be at most {MaxDomainLength} characters.");
            else if (p is not null && !serviceKeys.Contains(k.Domain.Trim())) E($"domain '{k.Domain}' is not a service key of the profile ({string.Join(", ", serviceKeys)}).");
            if (k.Stage is null || k.Stage.Length > 64) E("stage must be \"\" or at most 64 characters.");
            var direction = OneOf(k.Direction, ["Increase", "Decrease", "Either"], "direction", E);
            OneOf(k.Aggregation, ["P95", "P50", "Mean", "Count", "Value"], "aggregation", E);
            var method = OneOf(k.DetectionMethod, ["StaticThreshold", "BaselinePercent", "RobustZScore"], "detectionMethod", E);
            var frequency = Span(k.EvaluationFrequency, "evaluationFrequency", E);
            var bucket = Span(k.BucketSize, "bucketSize", E);
            var lookback = Span(k.LookbackWindow, "lookbackWindow", E);
            if (frequency is { } fr && fr != RequiredFrequency) E($"evaluationFrequency must be 00:01:00 in the demo (got {k.EvaluationFrequency}).");
            if (bucket is { } bu && bu != RequiredBucket) E($"bucketSize must be 00:05:00 in the demo (got {k.BucketSize}).");
            if (lookback is { } lb && lb < MinLookback) E($"lookbackWindow must be at least 1.00:00:00 (got {k.LookbackWindow}).");
            if (k.SampleSize is < 0 or > 100_000) E("sampleSize must be 0..100000.");
            if (k.MinObservationsPerBucket < 0) E("minObservationsPerBucket must be >= 0.");
            if (k.ConsecutiveCount is null or < 1 or > 48) E("consecutiveCount must be 1..48.");
            if (k.CooldownMinutes is null or < 0 or > 10_080) E("cooldownMinutes must be 0..10080.");
            if (k.Enabled != true) E("enabled must be true.");
            if (a is not null && !string.Equals(k.ProfileKey, a.ProfileKey, StringComparison.Ordinal)) E($"profileKey '{k.ProfileKey}' must be '{a.ProfileKey}'.");
            var grain = k.Grain?.Trim().ToLowerInvariant() ?? "domain";
            if (grain is not ("domain" or "entity")) E("grain must be domain or entity.");
            if (k.MaxEntitiesPerBucket is < 1 or > 5000) E("maxEntitiesPerBucket must be 1..5000.");
            if (method == "StaticThreshold" && direction == "Either") E("StaticThreshold does not support direction Either.");
            var history = MethodParams(k.MethodParamsJson, method, bucket, E);
            if (grain == "entity" && bucket is { } b && history is { } h)
            {
                var maxSpan = TimeSpan.FromTicks(Math.Max(1, EntityRetention.Ticks / b.Ticks - 1) * b.Ticks);
                if (h + b > maxSpan) E($"entity-grain KPIs keep 8 days; the method needs {h.TotalDays:0.##} days of history.");
            }

            var natural = $"{pillar}|{k.Kpi?.Trim()}|{k.Domain?.Trim()}|{k.Stage?.Trim()}|{grain}";
            if (!naturalKeys.Add(natural)) E("another KPI has the same pillar/kpi/domain/stage/grain.");

            var sourceKey = k.SourceKey?.Trim() ?? "";
            if (sourceKey.Length is 0 or > 64) E("sourceKey is required (max 64).");
            if (sourceKey.StartsWith("kql:", StringComparison.OrdinalIgnoreCase))
            {
                var queryKey = sourceKey[4..].Trim();
                if (!CustomKpiQueryKey().IsMatch(queryKey) || queryKey.Length > 128)
                    E($"sourceKey '{sourceKey}' must be kql:kpi:<name> with lowercase [a-z0-9:._-].");
                else if (!queryKeys.Add(queryKey)) E($"query key '{queryKey}' is used by more than one KPI; give each its own query.");
                ValidateKqlText(k, aliases, E);
            }
            else if (!AppInsightsSourceKeys.Contains(sourceKey, StringComparer.Ordinal))
            {
                E($"sourceKey '{sourceKey}' is not available in the demo. Use {string.Join(", ", AppInsightsSourceKeys)} or kql:kpi:<name>.");
            }
            else if (k.KqlText is not null)
            {
                E("kqlText is only meaningful for kql: KPIs.");
            }
        }
    }

    private static void ValidateKqlText(KpiDocument k, HashSet<string> profileAliases, Action<string> E)
    {
        var text = k.KqlText;
        if (string.IsNullOrWhiteSpace(text))
        {
            E("a kql: KPI needs kqlText (the catalog query seeded with PUT /api/settings/kql/{queryKey}).");
            return;
        }

        foreach (Match token in TokenPattern().Matches(text))
        {
            var name = token.Groups["name"].Value;
            if (KqlAllowedTokens.Contains(name, StringComparer.Ordinal)) continue;
            if (SourcePlaceholder().Match(token.Value) is { Success: true } source && source.Length == token.Length) continue;
            E($"kqlText uses '{token.Value}'; allowed: {string.Join(", ", KqlAllowedTokens.Select(t => "{{" + t + "}}"))} and {{{{SOURCE:alias}}}}.");
        }
        if (TokenPattern().Replace(text, "").Contains("{{", StringComparison.Ordinal)) E("kqlText has an unterminated '{{'.");

        var aliases = SourcePlaceholder().Matches(text).Select(m => m.Groups["alias"].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (aliases.Count == 0) E("kqlText must read telemetry through app('{{SOURCE:alias}}').");
        foreach (var alias in aliases.Where(x => !profileAliases.Contains(x)))
            E($"kqlText references alias '{alias}', which is not a telemetry source alias of this profile.");

        var code = StripLiterals(AllowedSourceCall().Replace(text, " "));
        foreach (var fn in CrossResourceFunctions)
            if (Regex.IsMatch(code, $@"(?<![\w]){Regex.Escape(fn)}\s*\(", RegexOptions.IgnoreCase))
                E($"kqlText calls {fn}(); a KPI query may only reference telemetry as app('{{{{SOURCE:alias}}}}').");
    }

    /// <summary>Blanks quoted literals and // comments so a function name inside a string is not mistaken for a call.</summary>
    private static string StripLiterals(string kql)
    {
        var sb = new System.Text.StringBuilder(kql.Length);
        for (var i = 0; i < kql.Length; i++)
        {
            var c = kql[i];
            if (c == '/' && i + 1 < kql.Length && kql[i + 1] == '/')
            {
                while (i < kql.Length && kql[i] != '\n') i++;
                sb.Append(' ');
                continue;
            }
            if (c is '\'' or '"')
            {
                var j = i + 1;
                while (j < kql.Length && kql[j] != c) j += kql[j] == '\\' ? 2 : 1;
                i = Math.Min(j, kql.Length);
                sb.Append(" \"\" ");
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static void ValidateKpiCoverage(Archetype a, ProfileDocument p, List<KpiDocument?> kpis, List<string> errors)
    {
        var rows = kpis.Where(k => k is not null).Select(k => k!).ToList();
        bool Has(string service, string sourceKey, string grain, string? aggregation) => rows.Any(k =>
            string.Equals(k.Domain?.Trim(), service, StringComparison.OrdinalIgnoreCase)
            && string.Equals(k.SourceKey?.Trim(), sourceKey, StringComparison.Ordinal)
            && string.Equals(k.Grain?.Trim() ?? "domain", grain, StringComparison.OrdinalIgnoreCase)
            && (aggregation is null || string.Equals(k.Aggregation?.Trim(), aggregation, StringComparison.OrdinalIgnoreCase)));

        foreach (var service in (p.Services ?? []).Where(s => !string.IsNullOrWhiteSpace(s?.Key)).Select(s => s!.Key!.Trim()))
        {
            foreach (var grain in new[] { "domain", "entity" })
            {
                if (!Has(service, "appinsights:latency", grain, "P95"))
                    errors.Add($"kpis.json: service '{service}' has no Latency P95 KPI (appinsights:latency, aggregation P95) with grain {grain}.");
                if (!Has(service, "appinsights:exceptions", grain, null))
                    errors.Add($"kpis.json: service '{service}' has no Exceptions KPI (appinsights:exceptions) with grain {grain}.");
            }
        }

        var events = (a.Steps ?? []).Select(s => s.BusinessEvent?.Name).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!).Distinct(StringComparer.Ordinal).ToList();
        var business = rows.Count(k =>
            TryPillar(k.Pillar, out var pillar) && pillar == "Business"
            && (k.SourceKey ?? "").Trim().StartsWith("kql:", StringComparison.OrdinalIgnoreCase)
            && k.KqlText is { } text && text.Contains("customEvents", StringComparison.Ordinal)
            && events.Any(e => text.Contains(e, StringComparison.Ordinal)));
        if (business < MinBusinessKqlKpis)
            errors.Add($"kpis.json: needs at least {MinBusinessKqlKpis} Business KPIs with a kql: source whose kqlText reads customEvents and names an archetype business event ({string.Join(", ", events)}); found {business}.");
    }

    private static bool TryPillar(string? value, out string pillar)
    {
        pillar = (value?.Trim() ?? "") switch
        {
            var v when v.Equals("Business", StringComparison.OrdinalIgnoreCase) || v.Equals("Business Health", StringComparison.OrdinalIgnoreCase) => "Business",
            var v when v.Equals("Application", StringComparison.OrdinalIgnoreCase) || v.Equals("Application Health", StringComparison.OrdinalIgnoreCase) => "Application",
            var v when v.Equals("Infrastructure", StringComparison.OrdinalIgnoreCase) || v.Equals("Infrastructure Health", StringComparison.OrdinalIgnoreCase) => "Infrastructure",
            _ => "",
        };
        return pillar.Length > 0;
    }

    private static string? OneOf(string? value, string[] names, string field, Action<string> E)
    {
        var match = names.FirstOrDefault(n => string.Equals(n, value?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (match is null) E($"{field} must be one of {string.Join(", ", names)}.");
        return match;
    }

    private static TimeSpan? Span(string? value, string field, Action<string> E)
    {
        if (!string.IsNullOrWhiteSpace(value) && value.Contains(':') && TimeSpan.TryParseExact(value.Trim(), "c", CultureInfo.InvariantCulture, out var parsed))
            return parsed;
        E($"{field} must be a time span (\"hh:mm:ss\" or \"d.hh:mm:ss\").");
        return null;
    }

    /// <summary>Validates the method parameters and returns the history the method needs (zero for StaticThreshold).</summary>
    private static TimeSpan? MethodParams(JsonElement? raw, string? method, TimeSpan? bucket, Action<string> E)
    {
        if (raw is not { } element || element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            E("methodParamsJson is required.");
            return null;
        }
        JsonDocument doc;
        try
        {
            doc = element.ValueKind == JsonValueKind.String ? JsonDocument.Parse(element.GetString() ?? "") : JsonDocument.Parse(element.GetRawText());
        }
        catch (JsonException)
        {
            E("methodParamsJson must be valid JSON.");
            return null;
        }
        using (doc)
        {
            var root = doc.RootElement;
            switch (method)
            {
                case "StaticThreshold":
                    if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("threshold", out var t) || t.ValueKind != JsonValueKind.Number)
                        E("StaticThreshold requires {\"threshold\": number}.");
                    return TimeSpan.Zero;
                case "BaselinePercent":
                    var days = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("lookbackDays", out var d) && d.TryGetInt32(out var dd) ? dd : 0;
                    if (days is < 1 or > 28) E("BaselinePercent requires lookbackDays 1..28.");
                    if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("thresholdPercent", out var p) || !p.TryGetDouble(out var pct) || pct <= 0)
                        E("BaselinePercent requires thresholdPercent > 0.");
                    return TimeSpan.FromDays(days);
                case "RobustZScore":
                    var window = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("windowBuckets", out var w) && w.TryGetInt32(out var wb) ? wb : 0;
                    if (window is < 4 or > 1000) E("RobustZScore requires windowBuckets 4..1000.");
                    if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("zThreshold", out var z) || !z.TryGetDouble(out var zt) || zt <= 0)
                        E("RobustZScore requires zThreshold > 0.");
                    return bucket is { } b ? TimeSpan.FromTicks(b.Ticks * window) : null;
                default:
                    return null;
            }
        }
    }

    // ------------------------------------------------------------------ corpus

    private static void ValidateCorpus(string packDir, List<string> errors)
    {
        var corpus = Path.Combine(packDir, "corpus");
        if (!Directory.Exists(corpus))
        {
            errors.Add($"corpus: missing; add corpus/<Domain>/*.md with at least {MinCorpusDocuments} synthetic RCAs.");
            return;
        }
        var files = Directory.GetFiles(corpus, "*.md", SearchOption.AllDirectories);
        foreach (var file in files.Where(f => string.Equals(Path.GetDirectoryName(f), corpus, StringComparison.OrdinalIgnoreCase)))
            errors.Add($"corpus: {Path.GetFileName(file)} is directly under corpus/; put it in corpus/<Domain>/ (the folder is the document's domain).");
        var rcas = files.Count(f => File.ReadAllText(f).Contains(RootCauseMarker, StringComparison.Ordinal));
        foreach (var file in files.Where(f => !File.ReadAllText(f).Contains(RootCauseMarker, StringComparison.Ordinal)))
            errors.Add($"corpus: {Path.GetRelativePath(corpus, file)} does not contain \"{RootCauseMarker}\".");
        if (rcas < MinCorpusDocuments)
            errors.Add($"corpus: {rcas} RCA document(s) contain \"{RootCauseMarker}\"; at least {MinCorpusDocuments} are required.");
    }

    /// <summary>
    /// Verbatim copy of <c>CompanyProfileValidator.FindUnsupportedRe2Construct</c> (JourneyMonitor.Core): the constructs
    /// RE2 (KQL <c>matches regex</c>) does not support, by name, or null. Keep in lockstep with the platform.
    /// </summary>
    public static string? FindUnsupportedRe2Construct(string pattern)
    {
        var text = pattern ?? "";
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\')
            {
                if (i + 1 >= text.Length) return null;
                var next = text[i + 1];
                if (next is >= '1' and <= '9') return $"backreference \\{next}";
                if (next == 'k' && i + 2 < text.Length && text[i + 2] is '<' or '\'' or '{') return "named backreference \\k";
                if (next == 'Z') return "end-of-string anchor \\Z (use $ or \\z)";
                if (next == 'G') return "contiguous-match anchor \\G";
                i++;
                continue;
            }

            if (c != '(' || i + 2 >= text.Length || text[i + 1] != '?') continue;
            var kind = text[i + 2];
            switch (kind)
            {
                case '=': return "lookahead (?=";
                case '!': return "negative lookahead (?!";
                case '>': return "atomic group (?>";
                case '(': return "conditional (?(";
                case '#': return "inline comment (?#";
                case '<' when i + 3 < text.Length && text[i + 3] == '=': return "lookbehind (?<=";
                case '<' when i + 3 < text.Length && text[i + 3] == '!': return "negative lookbehind (?<!";
            }

            var j = i + 2;
            while (j < text.Length && (char.IsAsciiLetter(text[j]) || text[j] == '-')) j++;
            if (j > i + 2 && j < text.Length && text[j] is ':' or ')')
            {
                var flags = text[(i + 2)..j];
                if (flags.Contains('n')) return "inline flag n (explicit capture)";
            }
        }
        return null;
    }

    private static bool Compiles(string pattern, bool ignoreCase)
    {
        try
        {
            _ = new Regex(pattern, (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None) | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
