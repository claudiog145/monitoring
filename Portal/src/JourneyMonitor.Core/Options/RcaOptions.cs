namespace JourneyMonitor.Core.Options;

/// <summary>
/// RCA Agent configuration. The agent composes one document per finished investigation and makes
/// exactly ONE LLM call to do it - it does not investigate, so there is no debate, no tool loop and
/// no per-agent roster to bound here.
/// </summary>
public sealed class RcaOptions
{
    public const string SectionName = "Rca";

    /// <summary>Master switch. Turning this off disables both the automatic and the on-demand route. Default true.</summary>
    public bool? Enabled { get; set; }

    /// <summary>
    /// Whether a finished investigation composes its RCA on its own. Default true. The rule is in
    /// <see cref="JourneyMonitor.Core.Monitoring.RcaAutoComposer"/>: escalated, with a probable
    /// root cause. Turning this off leaves the on-demand button as the only way in.
    /// </summary>
    public bool? AutoComposeOnEscalation { get; set; }

    /// <summary>
    /// Past incidents pulled from the knowledge base as context. Default 5 - enough to recognise a
    /// repeat, few enough that the prompt stays about THIS incident rather than drifting toward a
    /// similar one.
    /// </summary>
    public int? MaxPrecedents { get; set; }

    /// <summary>
    /// Output ceiling for the composed document. Default 4000: the template has eight sections plus
    /// a five-rung Whys chain, and a truncated RCA is unparseable - the same failure that cost the
    /// Inspector thirteen LLM calls when its synthesis hit exactly MaxTokensPerCall.
    /// </summary>
    public int? MaxTokens { get; set; }

    public bool IsEnabled => Enabled ?? true;

    public bool IsAutoComposeEnabled => AutoComposeOnEscalation ?? true;

    public int ResolvedMaxPrecedents => MaxPrecedents is > 0 ? MaxPrecedents.Value : 5;

    public int ResolvedMaxTokens => MaxTokens is > 0 ? MaxTokens.Value : 4000;
}
