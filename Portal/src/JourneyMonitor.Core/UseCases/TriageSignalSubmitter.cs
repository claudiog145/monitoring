using System.Runtime.ExceptionServices;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using Microsoft.Extensions.Logging;

namespace JourneyMonitor.Core.UseCases;

/// <summary>
/// Single entry point for a triage signal, whether it comes from a pillar daemon or from a person
/// (POST /api/triage/investigations). Creates the Queued investigation and hands it to the agent
/// work queue; an open investigation with the same dedup key absorbs the signal instead.
/// </summary>
public sealed class TriageSignalSubmitter(
    ITriageInvestigationStore store,
    IAgentWorkQueue queue,
    TimeProvider time,
    ILogger<TriageSignalSubmitter> logger) : ITriageSignalSubmitter
{
    public const int WorkItemPriority = 100;
    public const string PortalSourceMonkey = "portal";

    public async Task<TriageSubmission> SubmitAsync(TriageSignal signal, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(signal);
        var sourceMonkey = SourceMonkeyFor(signal);
        var dedupKey = TriageDedupKey.For(signal);

        var open = await store.FindOpenByDedupKeyAsync(dedupKey, ct);
        if (open is not null)
        {
            logger.LogInformation("[Triage] signal absorbed by open investigation {InvestigationId} (dedup key {DedupKey}).", open.InvestigationId, dedupKey);
            return new TriageSubmission(open.InvestigationId, Duplicate: true);
        }

        var id = Guid.NewGuid();
        var investigation = new TriageInvestigation(
            id, dedupKey, signal.AnomalyId, signal, TriageInvestigationStatus.Queued,
            Result: null, AlertLevel: null, Notified: false, CreatedAt: time.GetUtcNow(), CompletedAt: null, Error: null);
        try
        {
            await store.CreateAsync(investigation, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // CreateAsync failing does not prove nothing was written: another submitter may have won
            // the unique open-dedup index, or our own insert may have committed before the error.
            TriageInvestigation? winner;
            try
            {
                winner = await store.FindOpenByDedupKeyAsync(dedupKey, ct);
            }
            catch (Exception requeryEx)
            {
                logger.LogError(requeryEx, "[Triage] re-query after a failed insert also failed (dedup key {DedupKey}); rethrowing the insert error.", dedupKey);
                ExceptionDispatchInfo.Capture(ex).Throw();
                throw;
            }

            if (winner is null) throw;

            if (winner.InvestigationId != id)
            {
                logger.LogInformation("[Triage] concurrent submission absorbed by open investigation {InvestigationId} (dedup key {DedupKey}).", winner.InvestigationId, dedupKey);
                return new TriageSubmission(winner.InvestigationId, Duplicate: true);
            }

            // Our own row is there: the insert committed despite the error, so it still needs its work item.
            logger.LogWarning(ex, "[Triage] insert of investigation {InvestigationId} reported an error but committed; enqueuing it.", id);
        }

        try
        {
            await queue.EnqueueAsync(new AgentWorkItem
            {
                SourceMonkey = sourceMonkey,
                PayloadType = AgentWorkPayloadType.TriageSignal,
                PayloadId = id.ToString(),
                DedupKey = $"triage:{id}",
                Priority = WorkItemPriority,
            }, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Triage] enqueue failed for investigation {InvestigationId}.", id);
            await store.UpdateStatusAsync(id, TriageInvestigationStatus.Failed, $"enqueue failed: {ex.Message}", CancellationToken.None);
            throw;
        }

        logger.LogInformation("[Triage] investigation {InvestigationId} queued from {Source} ({Pillar}/{Kpi}).", id, signal.Source, signal.Pillar, signal.Kpi);
        return new TriageSubmission(id, Duplicate: false);
    }

    private static string SourceMonkeyFor(TriageSignal signal)
    {
        if (string.Equals(signal.Source, TriageSignalSources.Manual, StringComparison.OrdinalIgnoreCase)) return PortalSourceMonkey;
        if (!KpiPillarNames.TryParseDisplay(signal.Pillar, out var pillar))
            throw new ArgumentException($"Unknown pillar '{signal.Pillar}'.", nameof(signal));
        return pillar switch
        {
            KpiPillar.Application => MonkeyKey.ApplicationHealthMonitor,
            KpiPillar.Business => MonkeyKey.BusinessHealthMonitor,
            KpiPillar.Infrastructure => MonkeyKey.InfrastructureHealthMonitor,
            _ => throw new ArgumentOutOfRangeException(nameof(signal)),
        };
    }
}
