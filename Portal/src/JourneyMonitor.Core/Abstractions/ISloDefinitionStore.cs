using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Core.Abstractions;

/// <summary>
/// Durable, per-(domain, stage, signal type) SLO target store (see <c>monitoring.SloDefinition</c> /
/// <c>monitoring.SloDefinitionAudit</c>) - the Settings tab surface for what used to be a single
/// flat per-domain millisecond threshold (<see cref="ILatencySpikeSettingsStore"/>), replaced per
/// the data-driven finding in docs/product/wi-238036-threshold-research-and-wi-package-2026-09-29.md
/// that a single domain-wide number is wrong by up to 255x between a domain's fastest and slowest
/// stage. Same SQL-with-audit pattern as every other Settings store in this repo.
/// </summary>
public interface ISloDefinitionStore
{
    /// <summary>Every configured SLO definition, across all domains/stages/signal types.</summary>
    Task<IReadOnlyList<SloDefinition>> GetAllAsync(CancellationToken cancellationToken);

    /// <summary>Upserts one (Domain, Stage, SignalType) definition and appends an audit row in the same transaction.</summary>
    Task UpsertAsync(SloDefinition definition, string changedBy, CancellationToken cancellationToken);

    Task<IReadOnlyList<SloDefinitionAuditEntry>> GetAuditAsync(CancellationToken cancellationToken);
}
