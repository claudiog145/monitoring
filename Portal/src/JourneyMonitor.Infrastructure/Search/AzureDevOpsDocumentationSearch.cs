using System.Text.Json;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Options;

namespace JourneyMonitor.Infrastructure.Search;

/// <summary>
/// Documentation search backed by the indexes Azure DevOps already maintains over the wiki and the
/// work items.
///
/// Why no local index: a copy of someone else's wiki is a second thing to build, keep fresh and
/// secure, and it is stale the moment it is written. These endpoints are the same index the
/// organisation's own search box uses.
///
/// The search text is built by the caller from case fields, never written by a model - the same
/// rule the LogsAgent's tools follow, and for the same two reasons: a model-authored query against
/// someone else's systems is a cost surface and an attack surface.
/// </summary>
public sealed class AzureDevOpsDocumentationSearch(AzureDevOpsSearchTransport transport) : IDocumentationSearch
{
    public bool HasSource => transport.Settings.IsConfigured;

    public string NoSourceReason =>
        HasSource
            ? ""
            : "no documentation source is wired up (AzureDevOpsSearch is not configured), so this agent could not have found a runbook, " +
              "a wiki page or a recorded defect even if one exists. Nothing it reports is evidence about what is documented.";

    public async Task<DocumentSearchResult> SearchAsync(string terms, CancellationToken ct = default)
    {
        var settings = transport.Settings;
        if (!settings.IsConfigured) return DocumentSearchResult.Failed("Azure DevOps search is not configured.");
        if (string.IsNullOrWhiteSpace(terms)) return DocumentSearchResult.Ok([]);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.ResolvedTimeoutSeconds));

        // Independent on purpose: a wiki outage must not erase the defect that explains the case.
        var wiki = await SearchWikiAsync(settings, terms, timeout.Token);
        var workItems = await SearchWorkItemsAsync(settings, terms, timeout.Token);

        if (!wiki.Succeeded && !workItems.Succeeded)
        {
            return DocumentSearchResult.Failed($"wiki: {wiki.Error}; work items: {workItems.Error}");
        }

        return DocumentSearchResult.Ok(wiki.Hits.Concat(workItems.Hits).ToList());
    }

    private async Task<DocumentSearchResult> SearchWikiAsync(AzureDevOpsSearchOptions settings, string terms, CancellationToken ct)
    {
        var payload = Payload(terms, settings, new Dictionary<string, object?> { ["Project"] = new[] { settings.Project! } });

        using var response = await transport.PostAsync("wikisearchresults", payload, ct);
        if (!response.Succeeded || response.Document is null) return DocumentSearchResult.Failed(response.Error ?? "unknown error");

        var hits = new List<DocumentHit>();
        var root = response.Document.RootElement;
        if (root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
        {
            foreach (var result in results.EnumerateArray())
            {
                var path = ReadString(result, "path");
                var fileName = ReadString(result, "fileName");
                if (path.Length == 0 && fileName.Length == 0) continue;

                // Wiki paths come back URL-encoded with '-' standing in for spaces. The readable
                // form is what a model will cite, so that is the reference; the raw path is kept
                // because it is the one that actually resolves.
                var readable = ToReadableWikiPath(path.Length > 0 ? path : fileName);
                hits.Add(new DocumentHit(
                    DocumentHitKind.WikiPage,
                    Reference: readable,
                    Title: TitleFromWikiPath(readable),
                    Path: path.Length > 0 ? path : fileName,
                    Status: null,
                    Url: null));
            }
        }
        return DocumentSearchResult.Ok(hits);
    }

    private async Task<DocumentSearchResult> SearchWorkItemsAsync(AzureDevOpsSearchOptions settings, string terms, CancellationToken ct)
    {
        // The work item index uses the field name as the filter key, unlike the wiki and code
        // indexes which use "Project". Passing "Project" here returns HTTP 400 "Unknown filter".
        var payload = Payload(terms, settings, new Dictionary<string, object?> { ["System.TeamProject"] = new[] { settings.Project! } });

        using var response = await transport.PostAsync("workitemsearchresults", payload, ct);
        if (!response.Succeeded || response.Document is null) return DocumentSearchResult.Failed(response.Error ?? "unknown error");

        var hits = new List<DocumentHit>();
        var root = response.Document.RootElement;
        if (root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
        {
            foreach (var result in results.EnumerateArray())
            {
                if (!result.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Object) continue;

                var id = ReadString(fields, "system.id");
                if (id.Length == 0) continue;

                var type = ReadString(fields, "system.workitemtype");
                var state = ReadString(fields, "system.state");
                hits.Add(new DocumentHit(
                    DocumentHitKind.WorkItem,
                    Reference: $"#{id}",
                    Title: ReadString(fields, "system.title"),
                    Path: type.Length > 0 ? type : null,
                    // Carried because it changes the meaning entirely: a closed defect describing
                    // this failure is a precedent, an open one is the same problem still running.
                    Status: state.Length > 0 ? state : null,
                    Url: $"https://dev.azure.com/{settings.Organization}/{settings.Project}/_workitems/edit/{id}"));
            }
        }
        return DocumentSearchResult.Ok(hits);
    }

    private static Dictionary<string, object?> Payload(string terms, AzureDevOpsSearchOptions settings, Dictionary<string, object?> filters) =>
        new()
        {
            ["searchText"] = terms,
            ["$skip"] = 0,
            ["$top"] = settings.ResolvedMaxResults,
            ["filters"] = filters,
            ["includeFacets"] = false,
        };

    private static string TitleFromWikiPath(string readablePath)
    {
        var name = readablePath.Length > 0 ? readablePath[(readablePath.LastIndexOf('/') + 1)..] : readablePath;
        return name.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? name[..^3] : name;
    }

    /// <summary>
    /// Unescape AFTER turning the separator hyphens into spaces. The order matters: a literal
    /// hyphen in a page name arrives as %2D, so unescaping first would turn it into a space too and
    /// silently rename the page.
    /// </summary>
    private static string ToReadableWikiPath(string value)
    {
        if (value.Length == 0) return value;
        try
        {
            return Uri.UnescapeDataString(value.Replace('-', ' '));
        }
        catch (UriFormatException)
        {
            return value;
        }
    }

    private static string ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
}
