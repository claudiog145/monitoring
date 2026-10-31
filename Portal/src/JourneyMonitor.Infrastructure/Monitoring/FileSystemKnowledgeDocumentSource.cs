using System.Runtime.CompilerServices;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Options;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Infrastructure.Monitoring;

/// <summary>
/// Reads the converted Markdown corpus from disk (docs/RCAS-md, mounted into the container - see
/// docker-compose). Streams rather than loading 257 documents into memory at once, since the
/// import embeds them one at a time anyway.
/// </summary>
public sealed class FileSystemKnowledgeDocumentSource(IOptions<KnowledgeImportOptions> options) : IKnowledgeDocumentSource
{
    private string Root => options.Value.ResolvedCorpusPath;

    public bool IsAvailable => !string.IsNullOrWhiteSpace(Root) && Directory.Exists(Root);

    public string Location => string.IsNullOrWhiteSpace(Root) ? "(not configured)" : Root;

    public async IAsyncEnumerable<KnowledgeDocumentFile> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!IsAvailable) yield break;

        // Ordered so a capped run (maxDocuments) is reproducible rather than filesystem-dependent.
        var files = Directory.EnumerateFiles(Root, "*.md", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal);

        foreach (var path in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The conversion report is our own output, not a precedent.
            var name = Path.GetFileName(path);
            if (name.StartsWith('_')) continue;

            var content = await File.ReadAllTextAsync(path, cancellationToken);
            var relative = Path.GetRelativePath(Root, path);

            yield return new KnowledgeDocumentFile(relative, content);
        }
    }
}
