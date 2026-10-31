using System.Diagnostics;
using System.Text.Json;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using Microsoft.Extensions.Logging;

namespace JourneyMonitor.Core.Monitoring.Inspector;

/// <summary>
/// Checks every artifact the synthesis cited against what the real system actually holds for this
/// case, before the verdict ships.
///
/// The whole point is the three-way outcome. "Checked and absent" means a model made it up;
/// "no index exists to check it against" means we cannot tell. Collapsing them into one boolean
/// either slanders a correct answer or launders a fabricated one, and the card's
/// CitationsVerified/CitationsTotal figure - the one number a confident-sounding model cannot fake
/// - would quietly stop meaning anything.
///
/// Deterministic on purpose: asking a model whether another model's citation is real just adds a
/// third opinion where a lookup belongs.
/// </summary>
public sealed class InspectorGroundingService(
    IAgentTranscriptRepository transcripts,
    InspectorRecorder recorder,
    ILogger<InspectorGroundingService> logger) : IGroundingService
{
    private const string GroundingRole = "Grounding";

    /// <summary>A model can emit an arbitrarily long citation list; verifying thousands of them would cost more than the investigation.</summary>
    private const int MaxCitations = 100;

    public async Task<IReadOnlyList<CitedArtifact>> VerifyAsync(InspectorContext context, string synthesisJson, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var index = CaseArtifactIndex.Build(context, await ReadToolResultsAsync(context, ct));
        var artifacts = new List<CitedArtifact>();

        using (var doc = InspectorJson.TryParseObject(synthesisJson))
        {
            if (doc is not null
                && InspectorKqlGuards.TryGetProperty(doc.RootElement, "citations", out var citations)
                && citations.ValueKind == JsonValueKind.Array)
            {
                foreach (var citation in citations.EnumerateArray().Take(MaxCitations))
                {
                    if (citation.ValueKind != JsonValueKind.Object) continue;

                    var kind = Normalize(InspectorJson.ReadString(citation, "kind", ArtifactKind.Other));
                    var value = InspectorJson.ReadString(citation, "value").Trim();
                    if (value.Length == 0) continue;

                    var citedBy = InspectorJson.ReadString(citation, "citedBy", "(unattributed)");
                    artifacts.Add(index.Verify(kind, value, citedBy));
                }
            }
            else if (doc is null)
            {
                logger.LogWarning("[InspectorMonkey] the synthesis could not be parsed for grounding - treating it as citing nothing.");
            }
        }

        stopwatch.Stop();
        var resultJson = JsonSerializer.Serialize(artifacts, InspectorJson.Options);
        await recorder.RecordDeterministicTurnAsync(
            context.Case, GroundingRole, AgentKind.Grounding,
            promptText: $"verify {artifacts.Count} cited artifact(s) against this case's own evidence\n{index.Describe()}",
            rawResponseText: resultJson,
            outcomeJson: resultJson,
            durationMs: stopwatch.ElapsedMilliseconds,
            ct);

        return artifacts;
    }

    /// <summary>
    /// What the LogsAgent's queries actually returned. Without this, an operation id a tool pulled
    /// out of Application Insights - a real one, which the model is right to quote - is reported as
    /// fabricated simply because the Scout's package did not happen to carry it. Transcript writes
    /// are best-effort by contract, so a lost row degrades a citation to "not found" rather than
    /// taking the investigation down; that is the one honest failure mode left here.
    /// </summary>
    private async Task<IReadOnlyList<AgentToolCall>> ReadToolResultsAsync(InspectorContext context, CancellationToken ct)
    {
        try
        {
            return await transcripts.GetToolCallsAsync(context.Case.Id, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "[InspectorMonkey] could not read the tool results for grounding - citations will only be checked against the package.");
            return [];
        }
    }

    /// <summary>Anything outside the known taxonomy is treated as <see cref="ArtifactKind.Other"/>, which is not verifiable - never as a kind we happen to have an index for.</summary>
    private static string Normalize(string kind) => kind.Trim().ToLowerInvariant() switch
    {
        ArtifactKind.FilePath => ArtifactKind.FilePath,
        ArtifactKind.Endpoint => ArtifactKind.Endpoint,
        ArtifactKind.OperationId => ArtifactKind.OperationId,
        ArtifactKind.ExceptionType => ArtifactKind.ExceptionType,
        ArtifactKind.KnowledgeChunk => ArtifactKind.KnowledgeChunk,
        ArtifactKind.Document => ArtifactKind.Document,
        _ => ArtifactKind.Other,
    };
}

/// <summary>
/// Everything this case is known to contain, assembled once per grounding pass from the package
/// the Scout collected and the blackboard the specialists filled.
///
/// An EMPTY index is the important case: it means there is nothing to check against, so every
/// citation of that kind is <see cref="ArtifactVerification.NotVerifiable"/>. Reporting those as
/// "not found" would accuse a model of inventing an operation id in a case whose board row simply
/// never carried any.
/// </summary>
internal sealed class CaseArtifactIndex
{
    private readonly HashSet<string> _operationIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _endpoints = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _exceptionTypes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _knowledgeChunks = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _documents = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _filePaths = new(StringComparer.OrdinalIgnoreCase);

    public static CaseArtifactIndex Build(InspectorContext context, IReadOnlyList<AgentToolCall> toolCalls)
    {
        var index = new CaseArtifactIndex();
        index.AddPackage(context.Package);
        index.AddBlackboard(context.Blackboard);
        foreach (var toolCall in toolCalls.Where(c => c.Success))
        {
            index.AddToolResult(toolCall.ResultJson);
        }
        return index;
    }

    public string Describe() =>
        $"index sizes: operationIds={_operationIds.Count}, endpoints={_endpoints.Count}, exceptionTypes={_exceptionTypes.Count}, knowledgeChunks={_knowledgeChunks.Count}, documents={_documents.Count}, filePaths={_filePaths.Count}";

    public CitedArtifact Verify(string kind, string value, string citedBy) => kind switch
    {
        ArtifactKind.OperationId => Check(kind, value, citedBy, _operationIds, "operation ids from the board row and the collected events"),
        ArtifactKind.Endpoint => Check(kind, value, citedBy, _endpoints, "paths and operation names from the board row and the collected events"),
        ArtifactKind.ExceptionType => Check(kind, value, citedBy, _exceptionTypes, "exception types from the board row and the collected events"),
        ArtifactKind.KnowledgeChunk => Check(kind, value, citedBy, _knowledgeChunks, "the chunks the MemoryAgent actually retrieved"),
        ArtifactKind.Document => Check(kind, value, citedBy, _documents, "the wiki pages and work items the DocsAgent actually returned"),
        // Empty index still means NotVerifiable rather than NotFound: with no code search wired up,
        // or none run for this case, a cited path can be neither confirmed nor refuted.
        ArtifactKind.FilePath => Check(kind, value, citedBy, _filePaths, "the source locations the CodeAgent actually returned"),
        _ => new CitedArtifact(kind, value, citedBy, ArtifactVerification.NotVerifiable,
            "there is no index for this kind of artifact, so it can be neither confirmed nor refuted."),
    };

    private static CitedArtifact Check(string kind, string value, string citedBy, HashSet<string> index, string indexDescription)
    {
        if (index.Count == 0)
        {
            return new CitedArtifact(kind, value, citedBy, ArtifactVerification.NotVerifiable,
                $"this case carries no {indexDescription}, so there is nothing to check this against.");
        }

        // Contains() rather than equality in both directions: a path is cited with or without its
        // query string, and an exception type with or without its namespace.
        var found = index.Contains(value)
            || index.Any(known => known.Contains(value, StringComparison.OrdinalIgnoreCase)
                               || value.Contains(known, StringComparison.OrdinalIgnoreCase));

        return found
            ? new CitedArtifact(kind, value, citedBy, ArtifactVerification.Verified, $"present in {indexDescription}.")
            : new CitedArtifact(kind, value, citedBy, ArtifactVerification.NotFound,
                $"checked against {indexDescription} ({index.Count} entries) and absent - this identifier was made up.");
    }

    private void AddPackage(InvestigationPackage package)
    {
        using (var doc = SafeParse(package.DeterministicJson))
        {
            if (doc is not null)
            {
                if (InspectorKqlGuards.TryGetProperty(doc.RootElement, "groupPayload", out var payload))
                {
                    AddAll(_operationIds, InspectorJson.ReadStringArray(payload, "Ops"));
                    AddAll(_endpoints, InspectorJson.ReadStringArray(payload, "Paths"));
                    AddAll(_endpoints, InspectorJson.ReadStringArray(payload, "SlowPaths"));
                    AddAll(_endpoints, InspectorJson.ReadStringArray(payload, "Operations"));
                }

                if (InspectorKqlGuards.TryGetProperty(doc.RootElement, "boardRow", out var boardRow))
                {
                    AddAll(_exceptionTypes, InspectorJson.ReadStringArray(boardRow, "ExceptionTypes"));
                    var stage = InspectorJson.ReadString(boardRow, "Stage");
                    if (stage.Length > 0) _endpoints.Add(stage);
                }
            }
        }

        AddRawEvidence(package);
    }

    /// <summary>The individual events the Scout collected - the only place a specific operation id a model quotes is likely to come from.</summary>
    private void AddRawEvidence(InvestigationPackage package)
    {
        using var envelope = SafeParse(package.RawEvidenceJson);
        if (envelope is null) return;
        if (!InspectorKqlGuards.TryGetProperty(envelope.RootElement, "rowsJson", out var rowsJson)) return;
        if (rowsJson.ValueKind != JsonValueKind.String) return;

        JsonDocument rows;
        try
        {
            rows = JsonDocument.Parse(rowsJson.GetString() ?? "[]");
        }
        catch (JsonException)
        {
            return;
        }

        using (rows)
        {
            if (rows.RootElement.ValueKind != JsonValueKind.Array) return;

            foreach (var row in rows.RootElement.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object) continue;
                Add(_operationIds, InspectorJson.ReadString(row, "OperationId"));
                Add(_endpoints, InspectorJson.ReadString(row, "Path"));
                Add(_endpoints, InspectorJson.ReadString(row, "Name"));
                Add(_exceptionTypes, InspectorJson.ReadString(row, "ExceptionType"));
                Add(_exceptionTypes, InspectorJson.ReadString(row, "ProblemId"));
            }
        }
    }

    /// <summary>Only what a specialist really retrieved counts - the same closed-set rule the Doctor Monkey applies to its own "chunk-N" citations.</summary>
    private void AddBlackboard(IReadOnlyList<BlackboardEntry> blackboard)
    {
        foreach (var entry in blackboard)
        {
            if (entry.FindingsJson is null) continue;

            if (entry.AgentRole == InspectorAgent.Memory) AddMemoryPrecedents(entry.FindingsJson);
            else if (entry.AgentRole == InspectorAgent.Docs) AddDocuments(entry.FindingsJson);
            else if (entry.AgentRole == InspectorAgent.Code) AddCodeLocations(entry.FindingsJson);
        }
    }

    private void AddMemoryPrecedents(string findingsJson)
    {
        using var doc = SafeParse(findingsJson);
        if (doc is null) return;
        if (!InspectorKqlGuards.TryGetProperty(doc.RootElement, "precedents", out var precedents)) return;
        if (precedents.ValueKind != JsonValueKind.Array) return;

        foreach (var precedent in precedents.EnumerateArray())
        {
            if (precedent.ValueKind != JsonValueKind.Object) continue;
            Add(_knowledgeChunks, InspectorJson.ReadString(precedent, "chunkId"));
            Add(_knowledgeChunks, InspectorJson.ReadString(precedent, "sourceRef"));
        }
    }

    /// <summary>
    /// The title is indexed alongside the reference because that is what a model quotes when it
    /// paraphrases a result, and refusing it would report a real page as fabricated.
    /// </summary>
    private void AddDocuments(string findingsJson)
    {
        using var doc = SafeParse(findingsJson);
        if (doc is null) return;
        if (!InspectorKqlGuards.TryGetProperty(doc.RootElement, "documents", out var documents)) return;
        if (documents.ValueKind != JsonValueKind.Array) return;

        foreach (var document in documents.EnumerateArray())
        {
            if (document.ValueKind != JsonValueKind.Object) continue;
            Add(_documents, InspectorJson.ReadString(document, "reference"));
            Add(_documents, InspectorJson.ReadString(document, "path"));
            Add(_documents, InspectorJson.ReadString(document, "title"));
        }
    }

    /// <summary>
    /// Same closed-set rule: only the locations the code search really returned. The bare file name
    /// is indexed next to the full path because a model quotes "RentalSearch.cs" far more often
    /// than the whole repository path, and refusing it would report a real file as fabricated.
    /// </summary>
    private void AddCodeLocations(string findingsJson)
    {
        using var doc = SafeParse(findingsJson);
        if (doc is null) return;
        if (!InspectorKqlGuards.TryGetProperty(doc.RootElement, "locations", out var locations)) return;
        if (locations.ValueKind != JsonValueKind.Array) return;

        foreach (var location in locations.EnumerateArray())
        {
            if (location.ValueKind != JsonValueKind.Object) continue;
            Add(_filePaths, InspectorJson.ReadString(location, "path"));
            Add(_filePaths, InspectorJson.ReadString(location, "fileName"));
        }
    }

    private static void AddAll(HashSet<string> set, IReadOnlyList<string> values)
    {
        foreach (var value in values) Add(set, value);
    }

    /// <summary>
    /// A tool result is a different shape per tool, so this walks the payload by property NAME
    /// rather than by schema - adding a tool must not silently stop its results from counting as
    /// evidence. Depth-bounded because the payload is produced by a query, not by this code.
    /// </summary>
    private void AddToolResult(string resultJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(resultJson);
            AddJsonArtifacts(doc.RootElement, 0);
        }
        catch (JsonException)
        {
            // A tool that returned something unparseable contributes no index entries, which keeps
            // its citations "not verifiable" rather than wrongly "not found".
        }
    }

    private void AddJsonArtifacts(JsonElement element, int depth)
    {
        if (depth > 8) return;

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        var target = TargetFor(property.Name);
                        if (target is not null) Add(target, property.Value.GetString());
                    }
                    else
                    {
                        AddJsonArtifacts(property.Value, depth + 1);
                    }
                }
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) AddJsonArtifacts(item, depth + 1);
                break;
        }
    }

    /// <summary>Deliberately narrow: a generic name like "type" or "value" would pull half the payload into the index and make verification meaningless.</summary>
    private HashSet<string>? TargetFor(string propertyName) => propertyName.ToLowerInvariant() switch
    {
        "operationid" or "operation_id" => _operationIds,
        "path" or "url" or "name" or "operationname" => _endpoints,
        "exceptiontype" or "problemid" or "outermosttype" => _exceptionTypes,
        _ => null,
    };
    private static void Add(HashSet<string> set, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) set.Add(value.Trim());
    }

    private static JsonDocument? SafeParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                doc.Dispose();
                return null;
            }
            return doc;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
