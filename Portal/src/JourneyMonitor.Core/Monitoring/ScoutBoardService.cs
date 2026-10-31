using System.Text;
using System.Text.Json;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Configuration;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Options;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Core.Monitoring;

/// <summary>
/// Where the text of one board comes from.
///
/// A seam rather than a direct call, because the board query is moving from an embedded .kql.txt
/// to something composed from the company profile, and the two must be swappable without this
/// service knowing which one answered. <see cref="EmbeddedScoutBoardQueryTextSource"/> is the
/// behaviour-preserving implementation; a profile-driven composer registers over it.
///
/// Returns the text with its <c>{{...}}</c> placeholders still in place:
/// <see cref="ScoutBoardService.Substitute"/> owns the window/bucket/filter substitution, and
/// <c>IKqlSourceResolver</c> owns <c>{{SOURCE:alias}}</c>. A composer that resolved either would
/// quietly take ownership of a decision that is made per run, not per profile.
/// </summary>
public interface IScoutBoardQueryTextSource
{
    Task<string> GetBoardQueryTextAsync(ScoutBoardQueryTextRequest request, CancellationToken ct = default);
}

/// <summary>
/// Which board, under which profile, for which run. The profile key is the RESOLVED one rather
/// than the configured one, so a composer and the classifier can never answer from two different
/// profiles. The run values travel too, because a composer that builds the query from a profile
/// needs them up front, while a source that returns embedded text simply ignores them.
/// </summary>
public sealed record ScoutBoardQueryTextRequest(
    string Board,
    string ProfileKey,
    DateTimeOffset WindowFrom,
    DateTimeOffset WindowTo,
    int BucketMinutes,
    int RowsPerEntity);

/// <summary>Today's behaviour: the board text embedded in the Infrastructure assembly.</summary>
public sealed class EmbeddedScoutBoardQueryTextSource(IKqlQueryTextProvider kqlProvider) : IScoutBoardQueryTextSource
{
    public Task<string> GetBoardQueryTextAsync(ScoutBoardQueryTextRequest request, CancellationToken ct = default) =>
        Task.FromResult(kqlProvider.GetScoutBoardKql(request.Board));
}

/// <summary>
/// Reads one of the Scout Monkey's priority boards: substitutes the workbook-style placeholders
/// into the board KQL, runs it through <see cref="ITelemetrySource"/>, and parses the result into
/// <see cref="ScoutBoardRow"/>.
///
/// The VOCABULARY of each row - which service it belongs to, which step of which flow, and who is
/// affected - comes from the company profile through <see cref="ICompanyAdapter"/>, not from
/// whatever the query text wrote into its own Vertical/Stage/Entity columns. That is the whole
/// point: the same board read under a different profile yields a different vocabulary, which is
/// what makes the pipeline company-independent.
///
/// The board text embeds full ARM resource paths for the Application Insights resources it unions,
/// so it is production-only by construction and the running identity needs read access to all of
/// them. The app id/tenant passed to the telemetry source is only the entry point of that union -
/// resolved from the Insights configuration of <see cref="ScoutOptions.ResolvedEntryDomain"/> so
/// it is not a magic string buried in logic.
///
/// Failures are deliberately NOT swallowed here: a board that cannot run, or a profile that cannot
/// be resolved, is not the same as a board with nothing on it, and ScoutMonkeyService records that
/// difference on the ScoutRun.
/// </summary>
public sealed class ScoutBoardService(
    ITelemetrySource telemetrySource,
    IKqlQueryTextProvider kqlProvider,
    IKqlSourceResolver sourceResolver,
    ICompanyAdapterProvider companyAdapters,
    IOptions<InsightsOptions> insightsOptions,
    IOptionsMonitor<ScoutOptions> scoutOptions,
    IScoutBoardQueryTextSource? boardQueryText = null) : IScoutBoardReader
{
    private readonly IScoutBoardQueryTextSource _boardQueryText =
        boardQueryText ?? new EmbeddedScoutBoardQueryTextSource(kqlProvider);

    public async Task<ScoutBoardResult> ReadAsync(ScoutBoardRequest request, CancellationToken ct = default)
    {
        var options = scoutOptions.Get(request.Board);

        // Before the query, not after: a board read costs ~40s, and a profile that cannot be
        // resolved would otherwise buy a full board nothing on it can classify.
        var adapter = await ResolveAdapterAsync(request.Board, options, ct);

        var source = InsightsSourceResolver.ResolveForDomain(
            insightsOptions.Value, options.ResolvedEntryDomain, options.ResolvedEntryEnvironment);

        var template = await _boardQueryText.GetBoardQueryTextAsync(
            new ScoutBoardQueryTextRequest(
                request.Board,
                adapter.ProfileKey,
                request.WindowFrom,
                request.WindowTo,
                request.BucketMinutes,
                request.TakePerClub),
            ct);

        var kql = Substitute(template, request);
        kql = await sourceResolver.ResolveAsync(kql, options.ResolvedEntryEnvironment, ct);
        var rows = await telemetrySource.QueryAsync(
            new TelemetryQueryRequest(kql, source.AppInsightsAppId, source.TenantId), ct);

        var parsed = rows.Select(r => ToBoardRow(request.Board, r, adapter)).Where(r => r is not null).Select(r => r!).ToList();
        return new ScoutBoardResult(parsed, kql);
    }

    /// <summary>
    /// Loud on purpose, and the precedent is explicit: <c>KqlSourceResolver</c> throws for the same
    /// reason. An unresolved SERVICE OWNER still ships an investigation, but a missing profile
    /// yields a board on which nothing classifies - and a board full of unclassified rows looks
    /// exactly like a quiet one. Returning an empty board here would publish a calm cycle over an
    /// outage.
    /// </summary>
    private async Task<ICompanyAdapter> ResolveAdapterAsync(string board, ScoutOptions options, CancellationToken ct)
    {
        try
        {
            return await companyAdapters.GetAsync(options.ResolvedCompanyProfileKey, ct);
        }
        catch (CompanyProfileResolutionException ex)
        {
            throw new CompanyProfileResolutionException(
                $"The \"{board}\" scout board cannot be classified: {ex.Message} " +
                $"Set {nameof(ScoutOptions.CompanyProfileKey)} on this scout's configuration section.");
        }
    }

    /// <summary>
    /// Replaces the {{...}} placeholders of the board text. Exposed for tests: getting this wrong
    /// does not fail loudly, it sends a query Application Insights rejects with a generic syntax
    /// error, so the substitution is asserted directly rather than only end to end.
    /// </summary>
    public static string Substitute(string kqlTemplate, ScoutBoardRequest request)
    {
        var kql = kqlTemplate
            .Replace("{{VERTICAL_B64}}", ToBase64(request.Vertical))
            .Replace("{{CLUB_FILTER_B64}}", ToBase64(request.ClubFilter))
            .Replace("{{BUCKET_MINUTES}}", request.BucketMinutes.ToString())
            .Replace("{{TAKE_PER_CLUB}}", request.TakePerClub.ToString())
            .Replace("{{TIME_START}}", ToKqlDatetime(request.WindowFrom))
            .Replace("{{TIME_END}}", ToKqlDatetime(request.WindowTo));

        KqlPlaceholders.AssertNoneLeft(kql, "Scout board KQL");

        return kql;
    }

    private static string ToBase64(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    private static string ToKqlDatetime(DateTimeOffset value) =>
        $"datetime({value.UtcDateTime:yyyy-MM-ddTHH:mm:ss.fff}Z)";

    private static ScoutBoardRow? ToBoardRow(string board, TelemetryJourneyRow row, ICompanyAdapter adapter)
    {
        var payloadJson = ReadJsonColumn(row, "GroupPayload");
        if (payloadJson is null) return null;

        using var payload = JsonDocument.Parse(payloadJson);
        var root = payload.RootElement;

        var caseId = ReadString(root, "CaseId");
        var groupKey = ReadString(root, "GroupKey");
        if (caseId.Length == 0 || groupKey.Length == 0) return null;

        // Start/End come from the payload rather than the row so the window always matches the
        // bucket the CaseId was built from, even if the projection changes.
        if (!TryReadDate(root, "Start", out var from) || !TryReadDate(root, "End", out var to)) return null;

        var boardEntity = ReadString(root, "Entity");
        if (boardEntity.Length == 0) boardEntity = row.GetString("ClubOrHost");

        var rawRowJson = JsonSerializer.Serialize(row);
        var classification = ScoutRowMapper.Classify(
            adapter, ScoutRowMapper.ToRowContext(rawRowJson, payloadJson, boardEntity));

        return new ScoutBoardRow(
            Board: board,
            // CaseId and GroupKey are passed through UNTOUCHED. They are the dedup key, they were
            // built by the query that produced this row, and re-deriving them in C# would make a
            // package impossible to find again under the key it was deduped against.
            CaseId: caseId,
            GroupKey: groupKey,
            Vertical: classification.ServiceLabel,
            EntityKey: classification.EntityKey,
            Stage: classification.StepLabel,
            // Source names the telemetry resource the row came from, not anything about the
            // company, so it is still the board's own answer.
            Source: Fallback(ReadString(root, "Source"), row.GetString("Source")),
            // The board emits the label with a leading space (it used to carry an emoji) - trim it
            // so downstream comparisons and UI labels are not whitespace-sensitive.
            Priority: Fallback(ReadString(root, "Priority"), row.GetString("Priority")).Trim(),
            PriorityRank: row.GetInt("PriorityRank"),
            Reason: Fallback(ReadString(root, "Reason"), row.GetString("Reason")).Trim(),
            WindowFrom: from,
            WindowTo: to,
            GroupPayloadJson: payloadJson,
            RawRowJson: rawRowJson);
    }

    private static string Fallback(string primary, string secondary) => primary.Length > 0 ? primary : secondary;

    /// <summary>
    /// GroupPayload is a JSON string in Kusto, but <c>AppInsightsTelemetrySource</c> parses
    /// JSON-looking strings into real objects on the way in - so the column arrives as either
    /// shape depending on that parsing, and both have to be handled.
    /// </summary>
    private static string? ReadJsonColumn(TelemetryJourneyRow row, string name)
    {
        var element = row.Get(name);
        if (element is null) return null;

        return element.Value.ValueKind switch
        {
            JsonValueKind.Object => element.Value.GetRawText(),
            JsonValueKind.String => element.Value.GetString() is { Length: > 0 } s ? s : null,
            _ => null,
        };
    }

    private static string ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() ?? ""
            : "";

    private static bool TryReadDate(JsonElement root, string name, out DateTimeOffset value)
    {
        value = default;
        if (!root.TryGetProperty(name, out var el)) return false;
        return el.ValueKind switch
        {
            JsonValueKind.String => DateTimeOffset.TryParse(el.GetString(), out value),
            _ => false,
        };
    }
}
