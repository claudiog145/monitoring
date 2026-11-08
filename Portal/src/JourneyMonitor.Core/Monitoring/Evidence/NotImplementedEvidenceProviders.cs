using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Core.Monitoring.Evidence;

/// <summary>Placeholder until Phase 3: the dimension is reported, explicitly unavailable, instead of silently missing.</summary>
public abstract class NotImplementedEvidenceProvider : IEvidenceProvider
{
    public const string Reason = "not implemented (Fase 3)";

    public abstract string Dimension { get; }

    public Task<EvidenceSection> CollectAsync(TriageSignal signal, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken ct = default) =>
        Task.FromResult(new EvidenceSection(Dimension, false, null, Reason));
}

public sealed class InfrastructureEvidenceProvider : NotImplementedEvidenceProvider
{
    public override string Dimension => TriageDimensions.InfrastructureHealth;
}

public sealed class CodeReliabilityEvidenceProvider : NotImplementedEvidenceProvider
{
    public override string Dimension => TriageDimensions.CodeReliability;
}

public sealed class RecentChangesEvidenceProvider : NotImplementedEvidenceProvider
{
    public override string Dimension => TriageDimensions.RecentChanges;
}
