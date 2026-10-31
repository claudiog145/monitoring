using System.Text.Json;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Options;

namespace JourneyMonitor.Infrastructure.Search;

/// <summary>
/// Code search backed by the index Azure DevOps already maintains over every repository.
///
/// Returns locations only. The decision on record is that no file content reaches this platform:
/// the agent transcript is persisted in SQL and served over HTTP, so pulling source into it would
/// put production code in this database as a side effect of diagnosing a latency spike.
///
/// Scope is every repository by decision. The cost is that the organisation holds legacy, forked
/// and proof-of-concept repositories, so a hit can be real code that no longer runs - and grounding
/// would confirm it, because the file genuinely exists. That is handled by reporting rather than
/// filtering: every hit carries its repository and a flag when the name says it is not live.
/// </summary>
public sealed class AzureDevOpsCodeSearch(AzureDevOpsSearchTransport transport) : ICodeSearch
{
    public bool HasSource => transport.Settings.IsConfigured;

    public string NoSourceReason =>
        HasSource
            ? ""
            : "no repository or code index is wired up (AzureDevOpsSearch is not configured), so this agent could not have found a file, " +
              "symbol or commit even if one were relevant. Nothing it reports is evidence about the code.";

    public async Task<CodeSearchResult> SearchAsync(string term, CancellationToken ct = default)
    {
        var settings = transport.Settings;
        if (!settings.IsConfigured) return CodeSearchResult.Failed("Azure DevOps search is not configured.");
        if (string.IsNullOrWhiteSpace(term)) return CodeSearchResult.Ok([]);

        var filters = new Dictionary<string, object?> { ["Project"] = new[] { settings.Project! } };
        var repositories = settings.ResolvedRepositories;
        // Absent rather than empty: an empty array is a filter matching nothing, which is the
        // opposite of "no restriction".
        if (repositories.Count > 0) filters["Repository"] = repositories.ToArray();

        var payload = new Dictionary<string, object?>
        {
            ["searchText"] = term,
            ["$skip"] = 0,
            ["$top"] = settings.ResolvedMaxResults,
            ["filters"] = filters,
            ["includeFacets"] = false,
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.ResolvedTimeoutSeconds));

        using var response = await transport.PostAsync("codesearchresults", payload, timeout.Token);
        if (!response.Succeeded || response.Document is null)
        {
            return CodeSearchResult.Failed(response.Error ?? "unknown error");
        }

        return CodeSearchResult.Ok(Parse(response.Document.RootElement, settings));
    }

    private static List<CodeHit> Parse(JsonElement root, AzureDevOpsSearchOptions settings)
    {
        var hits = new List<CodeHit>();
        if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) return hits;

        foreach (var result in results.EnumerateArray())
        {
            var path = ReadString(result, "path");
            if (path.Length == 0) continue;

            var repository = result.TryGetProperty("repository", out var repo) ? ReadString(repo, "name") : "";
            var kinds = ReadMatchKinds(result);

            hits.Add(new CodeHit(
                Repository: repository,
                Path: path,
                FileName: ReadString(result, "fileName"),
                IsDefinition: kinds.Contains("def", StringComparer.OrdinalIgnoreCase),
                MatchKinds: kinds,
                MatchCount: ReadMatchCount(result),
                RepositoryLooksNotLive: LooksNotLive(repository, settings),
                Url: repository.Length > 0
                    ? $"https://dev.azure.com/{settings.Organization}/{settings.Project}/_git/{Uri.EscapeDataString(repository)}?path={Uri.EscapeDataString(path)}"
                    : null));
        }

        // Definitions first: "this is where the symbol lives" and "this file mentions it in a
        // comment" are very different answers, and the useful one must not be below the noise.
        return hits
            .OrderByDescending(h => h.IsDefinition)
            .ThenBy(h => h.RepositoryLooksNotLive)
            .ThenByDescending(h => h.MatchCount)
            .ToList();
    }

    private static IReadOnlyList<string> ReadMatchKinds(JsonElement result)
    {
        if (!result.TryGetProperty("matches", out var matches) || matches.ValueKind != JsonValueKind.Object) return [];
        if (!matches.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return [];

        return content.EnumerateArray()
            .Select(m => ReadString(m, "type"))
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int ReadMatchCount(JsonElement result)
    {
        if (!result.TryGetProperty("matches", out var matches) || matches.ValueKind != JsonValueKind.Object) return 0;
        if (!matches.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return 0;
        return content.GetArrayLength();
    }

    private static bool LooksNotLive(string repository, AzureDevOpsSearchOptions settings) =>
        repository.Length > 0
        && settings.ArchivedRepositoryMarkers.Any(m => repository.Contains(m, StringComparison.OrdinalIgnoreCase));

    private static string ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
}
