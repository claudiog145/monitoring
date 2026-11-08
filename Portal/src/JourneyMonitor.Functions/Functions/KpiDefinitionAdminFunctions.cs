using System.Globalization;
using System.Net;
using System.Text.Json;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring.Ingestion;
using JourneyMonitor.Core.Monitoring.Kpi;
using JourneyMonitor.Core.Options;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Functions.Functions;

/// <summary>
/// Spec 1000 KPI definition admin for the Settings tab. Reads are anonymous; the write requires
/// X-Settings-Write-Secret (fail closed, same as UpsertLatencySpikeSetting) and is always audited
/// by <see cref="IKpiDefinitionStore.UpsertAsync"/>. Range rules come from <see cref="KpiDefinition.Validate"/>.
/// </summary>
public sealed class KpiDefinitionAdminFunctions(
    IKpiDefinitionStore store,
    IOptions<MonitoringOptions> monitoringOptions,
    TimeProvider time,
    ILogger<KpiDefinitionAdminFunctions> logger,
    KpiProfileResolver? profileResolver = null,
    ICompanyProfileStore? companyProfiles = null,
    KqlKpiDryRun? kqlDryRun = null,
    IKpiEvaluator? evaluator = null)
{
    private readonly KpiProfileResolver _profiles = profileResolver ?? new KpiProfileResolver();
    private readonly IKpiEvaluator _evaluator = evaluator ?? new KpiEvaluator();

    public const int DefaultAuditTop = 200;
    public const int MaxChangedByLength = 128;
    private const string AnonymousChangedBy = "anonymous";

    [Function("GetKpiDefinitions")]
    public async Task<HttpResponseData> GetAllAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "settings/kpis")] HttpRequestData req,
        CancellationToken ct)
    {
        try
        {
            var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
            var scope = await _profiles.ResolveReadAsync(query["profileKey"], ct);
            if (!scope.IsValid) return await Spec1000Http.BadRequestAsync(req, [scope.Error!]);

            var definitions = await store.GetAllAsync(scope.ProfileKey, ct);
            return await Spec1000Http.WriteJsonAsync(req, HttpStatusCode.OK, definitions);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GetKpiDefinitions failed unexpectedly");
            return await Spec1000Http.WriteJsonAsync(req, HttpStatusCode.InternalServerError, new { error = "Unexpected server error." });
        }
    }

    [Function("GetKpiDefinitionAudit")]
    public async Task<HttpResponseData> GetAuditAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "settings/kpis/{id:int}/audit")] HttpRequestData req,
        int id,
        CancellationToken ct)
    {
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        if (!Spec1000Http.TryReadIntInRange(query["top"], DefaultAuditTop, 1, Spec1000Http.MaxTop, out var top))
        {
            return await Spec1000Http.BadRequestAsync(req, [$"top must be an integer between 1 and {Spec1000Http.MaxTop}."]);
        }

        try
        {
            var audit = await store.GetAuditAsync(id, top, ct);
            return await Spec1000Http.WriteJsonAsync(req, HttpStatusCode.OK, audit);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GetKpiDefinitionAudit failed unexpectedly for KPI {Id}", id);
            return await Spec1000Http.WriteJsonAsync(req, HttpStatusCode.InternalServerError, new { error = "Unexpected server error." });
        }
    }

    [Function("UpsertKpiDefinition")]
    public async Task<HttpResponseData> UpsertAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "settings/kpis")] HttpRequestData req,
        CancellationToken ct)
    {
        if (!SettingsWriteSecretGuard.HasMatchingWriteSecret(req, monitoringOptions.Value.SettingsWriteSecret))
        {
            return await Spec1000Http.WriteJsonAsync(req, HttpStatusCode.Unauthorized, new { error = "Missing or invalid X-Settings-Write-Secret header. Monitoring:SettingsWriteSecret must be configured for this endpoint to accept writes." });
        }

        try
        {
            UpsertKpiDefinitionBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<UpsertKpiDefinitionBody>(req.Body, TriageJson.Options, ct);
            }
            catch (JsonException)
            {
                return await Spec1000Http.BadRequestAsync(req, ["Request body must be valid JSON with the KpiDefinition fields."]);
            }

            if (body is null)
            {
                return await Spec1000Http.BadRequestAsync(req, ["Request body must be valid JSON with the KpiDefinition fields."]);
            }

            var changedBy = string.IsNullOrWhiteSpace(body.ChangedBy) ? AnonymousChangedBy : body.ChangedBy.Trim();
            var (definition, errors) = body.ToDefinition(time.GetUtcNow(), changedBy);
            if (definition is not null && definition.Domain.Length > KpiDomainPolicy.MaxStoredLength)
            {
                // Storage is nvarchar(32): a longer domain would be truncated to a value that is not a service.
                errors.Add(KpiDomainPolicy.TooLongMessage);
                return await Spec1000Http.BadRequestAsync(req, errors);
            }

            KpiDefinition? existing = null;
            if (definition is not null && definition.KpiDefinitionId > 0)
            {
                existing = await store.GetAsync(definition.KpiDefinitionId, ct);
                if (existing is null)
                    errors.Add($"KpiDefinitionId {definition.KpiDefinitionId} does not exist; use 0 to create a new KPI.");
                else
                    definition = body.KeepStoredGrain(definition, existing);
            }
            else if (definition is not null && string.IsNullOrWhiteSpace(body.ProfileKey))
            {
                var sameGrain = (await store.GetAllAsync(profileKey: null, ct)).Where(d => d.Grain == definition.Grain).ToList();
                existing = KpiProfileResolver.FindByNaturalKey(sameGrain, definition, await _profiles.GetActiveAsync(ct));
            }

            var scope = await _profiles.ResolveUpsertAsync(body.ProfileKey, existing?.ProfileKey, ct);
            if (!scope.IsValid) errors.Add(scope.Error!);
            else if (definition is not null) definition = definition with { ProfileKey = scope.ProfileKey! };
            if (changedBy.Length > MaxChangedByLength) errors.Add($"changedBy must be at most {MaxChangedByLength} chars.");
            if (definition is not null) errors.AddRange(definition.Validate());
            if (definition is { Grain: KpiGrain.Entity })
            {
                var requiredHistory = _evaluator.GetRequiredHistory(definition);
                if (KpiEntityRetention.ExceedsRetention(requiredHistory, definition.BucketSize))
                    errors.Add(KpiEntityRetention.ExceedsRetentionMessage(requiredHistory));
            }
            if (definition is not null && scope.IsValid && definition.Domain.Length > 0
                && !await new KpiDomainGuard(companyProfiles, logger).IsAllowedAsync(definition, ct))
            {
                errors.Add(KpiDomainPolicy.NotAServiceMessage(definition.Domain, definition.ProfileKey));
            }
            if (definition is not null && errors.Count == 0)
            {
                // Friendly 400 for the common case. Two simultaneous writes of the same new key can
                // still both pass this read and one will hit UQ_monitoring_KpiDefinition_Key (500);
                // acceptable for a manual Settings edit, and the store stays consistent.
                var all = await store.GetAllAsync(definition.ProfileKey, ct);
                var clash = all.FirstOrDefault(d => d.KpiDefinitionId != definition.KpiDefinitionId
                    && d.Pillar == definition.Pillar
                    && d.Grain == definition.Grain
                    && string.Equals(d.Kpi, definition.Kpi, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(d.Domain, definition.Domain, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(d.Stage, definition.Stage, StringComparison.OrdinalIgnoreCase));
                // Domain grain keeps the v1 wording that existing clients and tests match on.
                if (clash is not null)
                    errors.Add(definition.Grain == KpiGrain.Domain
                        ? $"A KPI with the same Pillar/Kpi/Domain/Stage already exists (KpiDefinitionId {clash.KpiDefinitionId})."
                        : $"A KPI with the same Pillar/Kpi/Domain/Stage and grain entity already exists (KpiDefinitionId {clash.KpiDefinitionId}).");
            }
            if (definition is not null && errors.Count == 0
                && definition.SourceKey.StartsWith(KqlKpiContract.SourceKeyPrefix, StringComparison.OrdinalIgnoreCase))
            {
                // Contract v4 §5: a kql: KPI is saved only after a one-hour dry run of its catalog query.
                var dryRunError = kqlDryRun is null
                    ? $"SourceKey '{definition.SourceKey}': kql: KPIs cannot be validated on this host."
                    : await kqlDryRun.ValidateAsync(definition, ct);
                if (dryRunError is not null) errors.Add(dryRunError);
            }

            if (definition is null || errors.Count > 0)
            {
                return await Spec1000Http.BadRequestAsync(req, errors);
            }

            var saved = await store.UpsertAsync(definition, changedBy, ct);
            if (existing is not null && !string.Equals(existing.ProfileKey, saved.ProfileKey, StringComparison.OrdinalIgnoreCase))
            {
                // The store's audit row records the new ProfileKey; this line names the move explicitly.
                logger.LogInformation("KPI {Id} moved from profile {From} to {To} by {ChangedBy}",
                    saved.KpiDefinitionId, existing.ProfileKey, saved.ProfileKey, changedBy);
            }
            return await Spec1000Http.WriteJsonAsync(req, HttpStatusCode.OK, saved);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "UpsertKpiDefinition failed unexpectedly");
            return await Spec1000Http.WriteJsonAsync(req, HttpStatusCode.InternalServerError, new { error = "Unexpected server error." });
        }
    }

    /// <summary>Wire shape of a KpiDefinition plus changedBy. UpdatedAt/UpdatedBy are not read: the server stamps them.</summary>
    private sealed class UpsertKpiDefinitionBody
    {
        public int? KpiDefinitionId { get; set; }
        public string? Pillar { get; set; }
        public string? Kpi { get; set; }
        public string? Domain { get; set; }
        public string? Stage { get; set; }
        public string? SourceKey { get; set; }
        public string? Direction { get; set; }
        public string? Aggregation { get; set; }
        public string? EvaluationFrequency { get; set; }
        public string? BucketSize { get; set; }
        public string? LookbackWindow { get; set; }
        public int? SampleSize { get; set; }
        public int? MinObservationsPerBucket { get; set; }
        public string? DetectionMethod { get; set; }
        public JsonElement? MethodParamsJson { get; set; }
        public int? ConsecutiveCount { get; set; }
        public int? CooldownMinutes { get; set; }
        public bool? Enabled { get; set; }
        public string? ChangedBy { get; set; }
        public string? ProfileKey { get; set; }
        public JsonElement? Grain { get; set; }
        public int? MaxEntitiesPerBucket { get; set; }

        public (KpiDefinition? Definition, List<string> Errors) ToDefinition(DateTimeOffset now, string changedBy)
        {
            var errors = new List<string>();

            var id = KpiDefinitionId ?? 0;
            if (id < 0) errors.Add("KpiDefinitionId must be 0 (create) or an existing id.");

            KpiPillar pillar = default;
            if (!KpiPillarNames.TryParseDisplay(Pillar?.Trim(), out pillar))
                errors.Add("Pillar must be Business, Application or Infrastructure.");

            var direction = ParseEnum<KpiDirection>(Direction, "Direction", errors);
            var aggregation = ParseEnum<KpiAggregation>(Aggregation, "Aggregation", errors);
            var method = ParseEnum<DetectionMethodKind>(DetectionMethod, "DetectionMethod", errors);
            var frequency = ParseTimeSpan(EvaluationFrequency, "EvaluationFrequency", errors);
            var bucketSize = ParseTimeSpan(BucketSize, "BucketSize", errors);
            var lookback = ParseTimeSpan(LookbackWindow, "LookbackWindow", errors);
            var grain = ParseGrain(Grain, errors);

            string methodParams = "";
            if (MethodParamsJson is not { } raw || raw.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                errors.Add("MethodParamsJson is required.");
            else
                methodParams = raw.ValueKind == JsonValueKind.String ? raw.GetString() ?? "" : raw.GetRawText();

            if (errors.Count > 0) return (null, errors);

            var definition = new KpiDefinition(
                id,
                pillar,
                Kpi?.Trim() ?? "",
                Domain?.Trim() ?? "",
                Stage?.Trim() ?? "",
                SourceKey?.Trim() ?? "",
                direction,
                aggregation,
                frequency,
                bucketSize,
                lookback,
                SampleSize ?? 0,
                MinObservationsPerBucket ?? 0,
                method,
                methodParams,
                ConsecutiveCount ?? 0,
                CooldownMinutes ?? 0,
                Enabled ?? false,
                now,
                changedBy)
            {
                Grain = grain,
                MaxEntitiesPerBucket = MaxEntitiesPerBucket ?? 500,
            };
            return (definition, errors);
        }

        private static T ParseEnum<T>(string? value, string name, List<string> errors) where T : struct, Enum
        {
            if (!string.IsNullOrWhiteSpace(value)
                && Enum.TryParse<T>(value.Trim(), ignoreCase: true, out var parsed)
                && Enum.IsDefined(parsed)
                && !int.TryParse(value, out _))
            {
                return parsed;
            }

            errors.Add($"{name} must be one of {string.Join(", ", Enum.GetNames<T>())}.");
            return default;
        }

        /// <summary>An update that omits grain/maxEntitiesPerBucket keeps the stored values (older clients).</summary>
        public KpiDefinition KeepStoredGrain(KpiDefinition definition, KpiDefinition stored) => definition with
        {
            Grain = Grain is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } ? definition.Grain : stored.Grain,
            MaxEntitiesPerBucket = MaxEntitiesPerBucket ?? stored.MaxEntitiesPerBucket,
        };

        /// <summary>Contract v4 §1: "domain" | "entity" (case-insensitive); missing or null means domain.</summary>
        private static KpiGrain ParseGrain(JsonElement? value, List<string> errors)
        {
            if (value is not { } raw || raw.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return KpiGrain.Domain;
            var text = raw.ValueKind == JsonValueKind.String ? raw.GetString()?.Trim() : null;
            if (string.Equals(text, "domain", StringComparison.OrdinalIgnoreCase)) return KpiGrain.Domain;
            if (string.Equals(text, "entity", StringComparison.OrdinalIgnoreCase)) return KpiGrain.Entity;
            errors.Add("Grain must be domain or entity.");
            return KpiGrain.Domain;
        }

        private static TimeSpan ParseTimeSpan(string? value, string name, List<string> errors)
        {
            if (!string.IsNullOrWhiteSpace(value) && value.Contains(':')
                && TimeSpan.TryParseExact(value.Trim(), "c", CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }

            errors.Add($"{name} must be a time span (\"hh:mm:ss\" or \"d.hh:mm:ss\").");
            return default;
        }
    }
}
