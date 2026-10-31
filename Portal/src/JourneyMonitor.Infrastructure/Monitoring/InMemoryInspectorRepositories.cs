using System.Collections.Concurrent;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Infrastructure.Monitoring;

/// <summary>
/// Process-local Inspector stores, used when no JourneyStore connection string is configured so a
/// fresh clone still runs end to end. Same graceful-degradation contract as the other InMemory*
/// repositories in this folder: data is lost on restart, by design.
/// </summary>
/// <remarks>
/// The SQL path gets its atomicity from monitoring.usp_TryLeaseAgentWork, which claims in one
/// statement. Here there is no database to inherit that from, so "pick the best eligible item and
/// mark it" is held under a single lock: a concurrent dictionary would make each individual write
/// safe and still let two callers choose the same item, which in this queue means paying for the
/// same investigation twice.
/// </remarks>
public sealed class InMemoryAgentWorkQueue : IAgentWorkQueue
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, AgentWorkItem> _byId = [];
    private readonly HashSet<string> _dedupKeys = new(StringComparer.OrdinalIgnoreCase);

    public Task<bool> EnqueueAsync(AgentWorkItem item, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_dedupKeys.Add(item.DedupKey))
            {
                return Task.FromResult(false);
            }
            _byId[item.Id] = Snapshot(item);
            return Task.FromResult(true);
        }
    }

    public Task<AgentWorkItem?> TryLeaseAsync(string payloadType, string leasedBy, TimeSpan leaseDuration, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            var claimed = Eligible(payloadType, now).FirstOrDefault();
            if (claimed is null)
            {
                return Task.FromResult<AgentWorkItem?>(null);
            }

            claimed.Status = AgentWorkItemStatus.Leased;
            claimed.Attempts++;
            claimed.LeasedUntil = now.Add(leaseDuration);
            claimed.LeasedBy = leasedBy;
            // The previous attempt's end time no longer describes this item: it is in flight again.
            claimed.CompletedAt = null;
            return Task.FromResult<AgentWorkItem?>(Snapshot(claimed));
        }
    }

    public Task CompleteAsync(Guid itemId, string leasedBy, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_byId.TryGetValue(itemId, out var item) && item.LeasedBy == leasedBy)
            {
                item.Status = AgentWorkItemStatus.Done;
                item.CompletedAt = DateTimeOffset.UtcNow;
                item.LeasedUntil = null;
                item.LeasedBy = null;
            }
        }
        return Task.CompletedTask;
    }

    public Task FailAsync(Guid itemId, string leasedBy, string error, CancellationToken ct = default)
    {
        lock (_gate)
        {
            // Same fence as CompleteAsync: a stalled consumer whose item was already re-claimed must
            // not be able to overwrite the state of whoever replaced it.
            if (_byId.TryGetValue(itemId, out var item) && item.LeasedBy == leasedBy)
            {
                item.LastError = error;
                // Attempts was already incremented when the lease was taken, so ">=" is the out-of-attempts test.
                item.Status = item.Attempts >= item.MaxAttempts
                    ? AgentWorkItemStatus.Abandoned
                    : AgentWorkItemStatus.Failed;
                // Stamped on both outcomes: it means "when the last attempt ended", and FailedToday
                // could not be computed without it.
                item.CompletedAt = DateTimeOffset.UtcNow;
                item.LeasedUntil = null;
                item.LeasedBy = null;
            }
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AgentWorkItem>> GetQueuedAsync(string payloadType, int top, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            IReadOnlyList<AgentWorkItem> result = Eligible(payloadType, now)
                .Take(top)
                .Select(Snapshot)
                .ToList();
            return Task.FromResult(result);
        }
    }

    public Task<AgentWorkQueueStats> GetStatsAsync(string payloadType, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        // UtcDateTime on both sides: DateTimeOffset.Date is the date in the value's OWN offset, so
        // comparing it against a UTC date silently stops matching between midnight UTC and
        // midnight local - a window in which the day's counters would read as zero.
        var today = now.UtcDateTime.Date;
        lock (_gate)
        {
            var items = _byId.Values.Where(i => i.PayloadType == payloadType).ToList();
            if (items.Count == 0)
            {
                return Task.FromResult(AgentWorkQueueStats.Empty);
            }

            var eligible = items.Where(i => IsEligible(i, now)).ToList();
            return Task.FromResult(new AgentWorkQueueStats(
                // An expired lease counts as queued and not as leased: nobody is working on it, and
                // showing it as in-flight would hide a dead consumer behind a busy-looking number.
                Queued: eligible.Count,
                Leased: items.Count(i => i.Status == AgentWorkItemStatus.Leased
                                         && i.LeasedUntil is not null
                                         && i.LeasedUntil >= now),
                DoneToday: items.Count(i => i.Status == AgentWorkItemStatus.Done && i.CompletedAt?.UtcDateTime.Date == today),
                FailedToday: items.Count(i => i.Status == AgentWorkItemStatus.Failed && i.CompletedAt?.UtcDateTime.Date == today),
                AbandonedTotal: items.Count(i => i.Status == AgentWorkItemStatus.Abandoned),
                OldestQueuedAt: eligible.Count == 0 ? null : eligible.Min(i => i.EnqueuedAt))
            {
                DiscardedTotal = items.Count(i => i.Status == AgentWorkItemStatus.Discarded),
            });
        }
    }

    public Task<int> DiscardQueuedAsync(
        string payloadType,
        IReadOnlyCollection<Guid>? itemIds,
        string reason,
        string discardedBy,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        // Null means "everything waiting"; an explicitly empty selection means nothing, and must not
        // be allowed to widen into a full clear.
        var selected = itemIds is null ? null : new HashSet<Guid>(itemIds);
        lock (_gate)
        {
            // Mirrors usp_DiscardAgentWork: only claimable work, so an item a consumer holds a live
            // lease on keeps running instead of vanishing from a queue that cannot actually stop it.
            var targets = _byId.Values
                .Where(i => i.PayloadType == payloadType && IsEligible(i, now))
                .Where(i => selected is null || selected.Contains(i.Id))
                .ToList();

            foreach (var item in targets)
            {
                item.Status = AgentWorkItemStatus.Discarded;
                item.LastError = Truncate($"Discarded by {discardedBy}: {reason}", 1024);
                item.CompletedAt = now;
                item.LeasedUntil = null;
                item.LeasedBy = null;
            }

            return Task.FromResult(targets.Count);
        }
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    /// <summary>Mirrors the claim predicate and ordering of usp_TryLeaseAgentWork. Call under <see cref="_gate"/>.</summary>
    private IEnumerable<AgentWorkItem> Eligible(string payloadType, DateTimeOffset now) =>
        _byId.Values
            .Where(i => i.PayloadType == payloadType && IsEligible(i, now))
            .OrderByDescending(i => i.Priority)
            .ThenBy(i => i.EnqueuedAt);

    private static bool IsEligible(AgentWorkItem item, DateTimeOffset now) =>
        item.Status == AgentWorkItemStatus.Queued
        || (item.Status == AgentWorkItemStatus.Failed && item.Attempts < item.MaxAttempts)
        // The attempts guard is repeated here on purpose: a consumer that keeps crashing
        // mid-investigation never reaches FailAsync, so without it the item would be re-leased forever.
        || (item.Status == AgentWorkItemStatus.Leased
            && item.LeasedUntil is not null
            && item.LeasedUntil < now
            && item.Attempts < item.MaxAttempts);

    /// <summary>A row snapshot, like the SQL OUTPUT clause - a caller must never hold a reference to queue state.</summary>
    private static AgentWorkItem Snapshot(AgentWorkItem item) => new()
    {
        Id = item.Id,
        SourceMonkey = item.SourceMonkey,
        PayloadType = item.PayloadType,
        PayloadId = item.PayloadId,
        DedupKey = item.DedupKey,
        Priority = item.Priority,
        Status = item.Status,
        Attempts = item.Attempts,
        MaxAttempts = item.MaxAttempts,
        LeasedUntil = item.LeasedUntil,
        LeasedBy = item.LeasedBy,
        LastError = item.LastError,
        EnqueuedAt = item.EnqueuedAt,
        CompletedAt = item.CompletedAt,
    };
}

public sealed class InMemoryForensicCaseRepository : IForensicCaseRepository
{
    private readonly ConcurrentDictionary<Guid, ForensicCase> _byId = new();
    private readonly ConcurrentDictionary<Guid, Guid> _byPackageId = new();

    /// <summary>
    /// Rejects a second case for the same package, like UQ_monitoring_ForensicCase_PackageId does.
    /// Accepting it would let a re-leased item believe it opened a fresh investigation and then
    /// overwrite the first one's verdict.
    /// </summary>
    public Task<ForensicCase> AddAsync(ForensicCase forensicCase, CancellationToken ct = default)
    {
        if (!_byPackageId.TryAdd(forensicCase.InvestigationPackageId, forensicCase.Id))
        {
            throw new InvalidOperationException(
                $"A forensic case already exists for investigation package {forensicCase.InvestigationPackageId}.");
        }
        _byId[forensicCase.Id] = forensicCase;
        return Task.FromResult(forensicCase);
    }

    public Task<ForensicCase> UpdateAsync(ForensicCase forensicCase, CancellationToken ct = default)
    {
        // Like the UPDATE ... WHERE Id = @Id: an unknown id writes nothing instead of inserting.
        if (_byId.ContainsKey(forensicCase.Id))
        {
            _byId[forensicCase.Id] = forensicCase;
        }
        return Task.FromResult(forensicCase);
    }

    public Task UpdatePhaseAsync(Guid caseId, string phase, CancellationToken ct = default)
    {
        // Best-effort by contract, and guarded on CompletedAt so a late progress write cannot
        // resurrect a finished case into an in-flight-looking state.
        if (_byId.TryGetValue(caseId, out var forensicCase) && forensicCase.CompletedAt is null)
        {
            forensicCase.Phase = phase;
        }
        return Task.CompletedTask;
    }

    public Task<ForensicCase?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult(_byId.TryGetValue(id, out var c) ? c : null);

    public Task<ForensicCase?> GetByPackageIdAsync(Guid investigationPackageId, CancellationToken ct = default) =>
        Task.FromResult(_byPackageId.TryGetValue(investigationPackageId, out var id) && _byId.TryGetValue(id, out var c)
            ? c
            : null);

    public Task<IReadOnlyList<ForensicCase>> GetRecentAsync(
        int top, string? vertical = null, string? clubId = null, string? profileKey = null, CancellationToken ct = default)
    {
        IReadOnlyList<ForensicCase> result = _byId.Values
            .Where(c => MatchesVertical(c, vertical) && MatchesClub(c, clubId) && MatchesProfile(c, profileKey))
            .OrderByDescending(c => c.StartedAt)
            .Take(top)
            .ToList();
        return Task.FromResult(result);
    }

    /// <summary>The board writes "Hotels" where the filter sends "hotels"; matched de-pluralised, same as the SQL proc.</summary>
    private static bool MatchesVertical(ForensicCase c, string? vertical) =>
        string.IsNullOrWhiteSpace(vertical)
        || c.Vertical.Trim().ToLowerInvariant().TrimEnd('s') == vertical.Trim().ToLowerInvariant().TrimEnd('s');

    /// <summary>Either id identifies the club: hotels/cars carry ClubId, cruises/air carry the partner id.</summary>
    private static bool MatchesClub(ForensicCase c, string? clubId) =>
        string.IsNullOrWhiteSpace(clubId)
        || c.ClubId == clubId.Trim()
        || c.IcePartnerId == clubId.Trim();

    /// <summary>
    /// Exact match, like the proc: blank means "every company", and N'' is a real stored value (the
    /// cases written before attribution existed) rather than a wildcard. Filtering for real here on
    /// purpose - a fake that ignored the scope would turn a cross-customer leak into a green test.
    /// </summary>
    private static bool MatchesProfile(ForensicCase c, string? profileKey) =>
        string.IsNullOrWhiteSpace(profileKey) || c.ProfileKey == profileKey.Trim();

    public Task<InspectorStats> GetStatsAsync(string? profileKey = null, CancellationToken ct = default)
    {
        var all = _byId.Values.Where(c => MatchesProfile(c, profileKey)).ToList();
        if (all.Count == 0) return Task.FromResult(InspectorStats.Empty);

        // Same UTC-on-both-sides rule as the queue stats above: the Inspector's daily cap is
        // enforced against this number, so a local-date comparison would hand out a fresh budget
        // hours before the UTC day actually rolls over.
        var today = DateTimeOffset.UtcNow.UtcDateTime.Date;
        return Task.FromResult(new InspectorStats(
            // Counts every case ever opened, including running and failed ones: a case that broke
            // must not quietly disappear from the denominator.
            Investigations: all.Count,
            Resolved: all.Count(c => c.Status == ForensicCaseStatus.Resolved),
            Escalated: all.Count(c => c.Status == ForensicCaseStatus.Escalated),
            ClosedNoIssue: all.Count(c => c.Status == ForensicCaseStatus.ClosedNoIssue),
            Failed: all.Count(c => c.Status == ForensicCaseStatus.Failed),
            CitationsTotal: all.Sum(c => c.CitationsTotal),
            CitationsVerified: all.Sum(c => c.CitationsVerified),
            LlmCalls: all.Sum(c => c.LlmCalls),
            TokensIn: all.Sum(c => (long)c.TokensIn),
            TokensOut: all.Sum(c => (long)c.TokensOut),
            InvestigationsToday: all.Count(c => c.StartedAt.UtcDateTime.Date == today),
            FirstCaseAt: all.Min(c => c.StartedAt),
            LastCaseAt: all.Max(c => c.StartedAt)));
    }
}

public sealed class InMemoryAgentTranscriptRepository : IAgentTranscriptRepository
{
    private readonly object _gate = new();
    private readonly List<AgentTurn> _turns = [];
    private readonly List<AgentToolCall> _toolCalls = [];

    public Task<AgentTurn> AddTurnAsync(AgentTurn turn, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _turns.Add(turn);
        }
        return Task.FromResult(turn);
    }

    public Task AddToolCallAsync(AgentToolCall toolCall, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _toolCalls.Add(toolCall);
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AgentTurn>> GetTurnsAsync(Guid forensicCaseId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            IReadOnlyList<AgentTurn> result = _turns
                .Where(t => t.ForensicCaseId == forensicCaseId)
                .OrderBy(t => t.CreatedAt)
                .ThenBy(t => t.Round)
                .ThenBy(t => t.Hop)
                .ToList();
            return Task.FromResult(result);
        }
    }

    /// <summary>Tool calls hang off a turn, not off the case, so the case filter is a join here too.</summary>
    public Task<IReadOnlyList<AgentToolCall>> GetToolCallsAsync(Guid forensicCaseId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var turnIds = _turns
                .Where(t => t.ForensicCaseId == forensicCaseId)
                .Select(t => t.Id)
                .ToHashSet();

            IReadOnlyList<AgentToolCall> result = _toolCalls
                .Where(tc => turnIds.Contains(tc.AgentTurnId))
                .OrderBy(tc => tc.CreatedAt)
                .ToList();
            return Task.FromResult(result);
        }
    }
}
