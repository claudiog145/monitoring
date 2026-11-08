using System.Text;

namespace FakeAppInsights.Rewrite;

/// <summary>Replacements of exact character ranges of the original query, applied back to front.</summary>
internal sealed class TextEdits
{
    private readonly List<(int Start, int Length, string Text)> _edits = [];

    public void Replace(int start, int length, string text) => _edits.Add((start, length, text));

    public string ApplyTo(string original)
    {
        if (_edits.Count == 0) return original;

        var ordered = _edits.OrderBy(e => e.Start).ToList();
        for (var i = 1; i < ordered.Count; i++)
        {
            if (ordered[i].Start < ordered[i - 1].Start + ordered[i - 1].Length)
            {
                throw new InvalidOperationException("Overlapping KQL edits.");
            }
        }

        var builder = new StringBuilder(original);
        for (var i = ordered.Count - 1; i >= 0; i--)
        {
            var (start, length, text) = ordered[i];
            builder.Remove(start, length).Insert(start, text);
        }
        return builder.ToString();
    }
}
