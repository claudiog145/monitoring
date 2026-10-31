using System.Text.Json;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Core.UseCases;

/// <summary>
/// Turns a <see cref="Finding"/> into a channel-agnostic <see cref="NotificationRequest"/>.
///
/// This parsing used to live in the Infrastructure notifiers (AlertMessageFormatter), which forced
/// every channel to know what a Finding is. Knowing how to describe a Finding belongs to whoever
/// produced it; a channel only has to know how to transport a subject and a body.
/// </summary>
internal static class FindingNotificationRequestFactory
{
    internal static NotificationRequest From(Finding finding)
    {
        var (alertSeverity, note) = ParseEvidence(finding.EvidenceJson);

        var body = $"""
            {finding.Title}

            Domain: {finding.Domain}
            Stage: {finding.Stage}
            Severity: {finding.Severity} (alert severity: {alertSeverity})
            Finding id: {finding.Id}
            Correlation id: {finding.CorrelationId}

            {note}

            This finding has also been routed to the Doctor Monkey for root-cause diagnosis - see the Simian Army tab for the full analysis.
            """;

        return new NotificationRequest(
            NotificationSourceType.Finding,
            finding.Id,
            alertSeverity == AlertSeverity.Page ? NotificationSeverity.Critical : NotificationSeverity.Warning,
            $"[JourneyMonitor] {finding.Title}",
            body,
            []);
    }

    private static (string AlertSeverity, string Note) ParseEvidence(string evidenceJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(evidenceJson);
            var severity = doc.RootElement.TryGetProperty("alertSeverity", out var s) ? s.GetString() ?? "unknown" : "unknown";
            var note = doc.RootElement.TryGetProperty("note", out var n) ? n.GetString() ?? "" : "";
            return (severity, note);
        }
        catch (JsonException)
        {
            return ("unknown", "");
        }
    }
}
