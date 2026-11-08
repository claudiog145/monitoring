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
public sealed class FileSystemKnowledgeDocumentSource : IKnowledgeDocumentSource
{
    private readonly Func<string> _root;

    public FileSystemKnowledgeDocumentSource(IOptions<KnowledgeImportOptions> options)
    {
        _root = () => options.Value.ResolvedCorpusPath;
    }

    private FileSystemKnowledgeDocumentSource(string root)
    {
        _root = () => root;
    }

    private string Root => _root();

    public bool IsAvailable => !string.IsNullOrWhiteSpace(Root) && Directory.Exists(Root);

    public string Location => string.IsNullOrWhiteSpace(Root) ? "(not configured)" : Root;

    /// <inheritdoc />
    public IKnowledgeDocumentSource? Scope(string subPath)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(subPath)) return null;

        var root = Path.GetFullPath(Root);
        var scoped = Path.GetFullPath(Path.Combine(root, subPath.Replace('\\', '/').Replace('/', Path.DirectorySeparatorChar)));

        // Defence in depth: the caller already rejected '..', but a sub-folder must never resolve outside the corpus.
        var rootWithSeparator = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        if (!scoped.StartsWith(rootWithSeparator, StringComparison.Ordinal) || !Directory.Exists(scoped)) return null;

        return new FileSystemKnowledgeDocumentSource(scoped);
    }

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
