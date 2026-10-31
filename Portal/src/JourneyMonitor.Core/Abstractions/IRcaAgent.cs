using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Core.Abstractions;

/// <summary>
/// Composes the publishable RCA for a finished investigation.
///
/// It does NOT investigate. That was a deliberate choice (2026-10-15): the Inspector already
/// produces root causes and measurably got better at it once CodeAgent/DocsAgent were connected
/// (0 of 7 cases with a root cause, then 2 of 8, average confidence 0.12 to 0.50). A second agent
/// re-reading the same telemetry would escalate with the same confidence and double the cost. What
/// was missing was the document, not the analysis.
/// </summary>
public interface IRcaAgent
{
    Task<RcaReport> ComposeAsync(ForensicCase forensicCase, CancellationToken ct = default);
}
