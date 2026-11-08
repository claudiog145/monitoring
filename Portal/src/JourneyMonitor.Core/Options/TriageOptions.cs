namespace JourneyMonitor.Core.Options;

public enum TriageMode { Off, Shadow, Live }
public enum TriageEngine { Inspector, FoundryAgent }

public sealed class TriageOptions
{
    public const string SectionName = "Triage";
    public TriageMode Mode { get; set; } = TriageMode.Shadow;
    public TriageEngine Engine { get; set; } = TriageEngine.Inspector;
    /// <summary>Evidence window is [DetectedAt - EvidenceWindowMinutes, DetectedAt + 15 min].</summary>
    public int EvidenceWindowMinutes { get; set; } = 180;
    /// <summary>Agent key passed to the LLM router and the budget store.</summary>
    public string AgentKey { get; set; } = "triage-monkey";
}
