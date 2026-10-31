using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace JourneyMonitor.Core.Domain;

/// <summary>
/// One addressable piece of a source document. The section title travels with the body because
/// "Root Cause" and "Timeline" answer different questions - collapsing a document into one vector
/// averages them into something that answers neither.
/// </summary>
public sealed record KnowledgeSection(string Title, string Body);

/// <summary>
/// A source document after parsing. <see cref="ControlCharactersRemoved"/> is reported rather than
/// silently swallowed: PDF extraction drops ligatures and leaves control bytes mid-word
/// ("Scottsdale" arrives as "Sco\0sdale"), so a document with many of them has degraded text and
/// the operator should know before trusting a citation from it. The lost letters are NOT guessed
/// back - removing what is broken is cleaning; inventing what is missing is fabrication.
/// </summary>
public sealed record ParsedKnowledgeDocument(
    string RelativePath,
    string Domain,
    string Title,
    string SourceType,
    IReadOnlyList<KnowledgeSection> Sections,
    int ControlCharactersRemoved);

/// <summary>
/// Turns a converted Markdown document (see docs/RCAS-md, produced by markitdown) into labelled,
/// section-sized chunks.
///
/// The corpus holds two genuinely different things and they must not be indexed as one: 40 formal
/// RCAs written after an incident ("the API server entered a degraded state" - a confirmed cause)
/// and 185 exported tickets ("the user could not search flights" - a reported symptom). Citing a
/// symptom as if it were a validated cause is exactly the failure this platform's grounding exists
/// to prevent, so the distinction is carried in <see cref="ParsedKnowledgeDocument.SourceType"/>
/// all the way to the citation.
/// </summary>
public static class KnowledgeDocumentParser
{
    /// <summary>Sections longer than this are split; big enough to keep a 5-Whys table whole, small enough that one vector still means something.</summary>
    public const int MaxChunkChars = 1800;

    /// <summary>Carried between split parts so a sentence cut in half is still findable from either side.</summary>
    private const int ChunkOverlapChars = 150;

    /// <summary>Below this a "section" is a stray heading, not content.</summary>
    private const int MinSectionChars = 40;

    // markitdown renders Word headings as bold-only lines rather than '#' - both forms appear.
    private static readonly Regex BoldHeading = new(@"^\*\*(?<title>[^*].{0,120}?)\*\*:?\s*$", RegexOptions.Compiled);
    private static readonly Regex HashHeading = new(@"^#{1,6}\s+(?<title>.{1,120})$", RegexOptions.Compiled);
    private static readonly Regex ControlChars = new(@"[\u0000-\u0008\u000B\u000C\u000E-\u001F]", RegexOptions.Compiled);

    public static ParsedKnowledgeDocument Parse(string relativePath, string rawText)
    {
        var removed = ControlChars.Matches(rawText).Count;
        var text = ControlChars.Replace(rawText, "");

        var domain = ReadDomain(relativePath);
        var title = ReadTitle(relativePath);
        var sourceType = Classify(text);

        var sections = SplitIntoSections(text);
        if (sections.Count == 0)
        {
            sections = SplitBySize("Document", text);
        }

        return new ParsedKnowledgeDocument(relativePath, domain, title, sourceType, sections, removed);
    }

    /// <summary>
    /// The folder is the only domain label this corpus carries (Air, Cruise, NG-Hotel, Telecom...),
    /// which is why the conversion mirrored the directory structure instead of flattening it.
    /// </summary>
    private static string ReadDomain(string relativePath)
    {
        var separator = relativePath.IndexOfAny(['/', '\\']);
        return separator <= 0 ? "unclassified" : relativePath[..separator];
    }

    private static string ReadTitle(string relativePath)
    {
        var name = relativePath.Replace('\\', '/').Split('/')[^1];
        var dot = name.LastIndexOf('.');
        return (dot > 0 ? name[..dot] : name).Trim();
    }

    /// <summary>
    /// A formal RCA follows the corporate template; a ticket is an export with its own header. A
    /// document matching neither is labelled <see cref="KnowledgeSourceType.IncidentDocument"/>
    /// rather than forced into the closest bucket - 32 of them do not fit, and pretending they are
    /// RCAs would put unreviewed text behind a label that means "a human concluded this".
    /// </summary>
    private static string Classify(string text)
    {
        var hasRcaTitle = text.Contains("root cause analysis", StringComparison.OrdinalIgnoreCase);
        var hasRcaSections =
            text.Contains("bottom line up front", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("5 whys", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("what happened", StringComparison.OrdinalIgnoreCase);

        if (hasRcaTitle && hasRcaSections) return KnowledgeSourceType.RcaDocument;

        var looksLikeTicket =
            text.Contains("Incident Details Report", StringComparison.OrdinalIgnoreCase) ||
            (text.Contains("ID:", StringComparison.Ordinal) && text.Contains("Priority:", StringComparison.Ordinal)) ||
            text.Contains("RED ALERT Incident", StringComparison.OrdinalIgnoreCase);

        return looksLikeTicket ? KnowledgeSourceType.IncidentTicket : KnowledgeSourceType.IncidentDocument;
    }

    private static List<KnowledgeSection> SplitIntoSections(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var sections = new List<KnowledgeSection>();

        var currentTitle = "";
        var buffer = new StringBuilder();

        void Flush()
        {
            var body = buffer.ToString().Trim();
            buffer.Clear();
            if (body.Length < MinSectionChars) return;

            var heading = string.IsNullOrWhiteSpace(currentTitle) ? "Document" : currentTitle;
            sections.AddRange(SplitBySize(heading, body));
        }

        foreach (var line in lines)
        {
            var heading = ReadHeading(line);
            if (heading is not null)
            {
                Flush();
                currentTitle = heading;
                continue;
            }

            buffer.AppendLine(line);
        }

        Flush();
        return sections;
    }

    private static string? ReadHeading(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0) return null;

        var hash = HashHeading.Match(trimmed);
        if (hash.Success) return Clean(hash.Groups["title"].Value);

        var bold = BoldHeading.Match(trimmed);
        // A bold line is only a heading when it is short - a whole bolded paragraph is content.
        if (bold.Success && bold.Groups["title"].Value.Trim().Length <= 80) return Clean(bold.Groups["title"].Value);

        return null;
    }

    private static string Clean(string value) => value.Replace("*", "").Trim().TrimEnd(':').Trim();

    /// <summary>Splits on paragraph boundaries where possible so a chunk does not start mid-sentence.</summary>
    private static List<KnowledgeSection> SplitBySize(string title, string body)
    {
        var result = new List<KnowledgeSection>();
        if (body.Length <= MaxChunkChars)
        {
            if (body.Trim().Length >= MinSectionChars) result.Add(new KnowledgeSection(title, body.Trim()));
            return result;
        }

        var position = 0;
        var part = 1;
        while (position < body.Length)
        {
            var length = Math.Min(MaxChunkChars, body.Length - position);
            var isLast = position + length >= body.Length;
            var slice = body.Substring(position, length);

            if (!isLast)
            {
                var breakAt = slice.LastIndexOf("\n\n", StringComparison.Ordinal);
                if (breakAt < MaxChunkChars / 2) breakAt = slice.LastIndexOf('\n');
                if (breakAt > MaxChunkChars / 2) slice = slice[..breakAt];
            }

            var trimmed = slice.Trim();
            if (trimmed.Length >= MinSectionChars)
            {
                result.Add(new KnowledgeSection($"{title} (part {part})", trimmed));
                part++;
            }

            if (isLast) break;

            // The step must stay a real fraction of the slice. Taking slice.Length - overlap
            // directly collapses to 1 once the remaining text is shorter than the overlap, and the
            // loop then walks one character at a time - a single document produced 79 chunks that
            // way, each a near-copy of the last.
            position += Math.Max(MaxChunkChars / 2, slice.Length - ChunkOverlapChars);
        }

        return result;
    }

    /// <summary>
    /// Stable identity for a chunk's text, used to keep the eleven duplicated documents
    /// ("- Copy", "(1)") from counting twice in cosine retrieval - the same precedent indexed
    /// twice silently doubles its weight. Hashes the normalized text, so whitespace differences
    /// between two exports of the same document still collapse to one row.
    /// </summary>
    public static string ContentHash(string body)
    {
        var normalized = Regex.Replace(body.Trim(), @"\s+", " ").ToLowerInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }
}
