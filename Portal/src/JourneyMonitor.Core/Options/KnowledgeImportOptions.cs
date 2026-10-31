namespace JourneyMonitor.Core.Options;

/// <summary>
/// Where the converted RCA/incident corpus lives and how much of it one import run may process.
/// The cap exists because importing is the only operation in this system that makes thousands of
/// embedding calls in a row: 257 documents at roughly eight sections each is ~2,000 calls, and
/// that should be a deliberate act, not something a timer does by accident.
/// </summary>
public sealed class KnowledgeImportOptions
{
    public const string SectionName = "KnowledgeImport";

    /// <summary>Path to the converted Markdown corpus. In Docker this is the mounted docs/RCAS-md.</summary>
    public string? CorpusPath { get; set; }

    public const string DefaultCorpusPath = "/corpus";

    /// <summary>Documents per run. Default 500 - above the current corpus, so a normal run is not silently truncated.</summary>
    public int? MaxDocumentsPerRun { get; set; }

    public const int DefaultMaxDocumentsPerRun = 500;

    public string ResolvedCorpusPath =>
        string.IsNullOrWhiteSpace(CorpusPath) ? DefaultCorpusPath : CorpusPath.Trim();

    public int ResolvedMaxDocumentsPerRun =>
        MaxDocumentsPerRun is > 0 ? MaxDocumentsPerRun.Value : DefaultMaxDocumentsPerRun;
}
