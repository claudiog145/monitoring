namespace JourneyMonitor.Core.Options;

/// <summary>
/// Connection to the durable journey store (the per-vertical hotel/car/cruise/air schemas).
/// Distinct from the process-local 90-second <c>ITelemetryQueryCache</c>: this holds the
/// snapshots the portal reads.
///
/// Left empty on a fresh clone, in which case ingestion and stored reads report
/// "not configured" instead of failing - the live endpoint keeps working untouched.
/// </summary>
public sealed class JourneyStoreOptions
{
    public const string SectionName = "JourneyStore";

    /// <summary>
    /// App Configuration's platform-reliability-database-connectionString, with its hyphens as
    /// underscores because az containerapp rejects env var names outside a-z, A-Z, 0-9 and _.
    /// Takes precedence over the section above.
    /// </summary>
    public const string AppConfigKey = "platform_reliability_database_connectionString";

    public string? ConnectionString { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ConnectionString);
}
