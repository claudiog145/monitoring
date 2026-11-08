using JourneyMonitor.Core.Company;

namespace JourneyMonitor.Core.Monitoring.Privacy;

/// <summary>
/// Contract v4 §5: runs on every evidence section and every prompt sent by Triage and Inspector.
/// CountsByPattern holds how many occurrences each pattern hid - never the values themselves.
/// </summary>
public interface IEvidenceRedactor
{
    RedactionResult Redact(string text, CompanyProfile profile);
}

public sealed record RedactionResult(string Text, IReadOnlyDictionary<string, int> CountsByPattern);
