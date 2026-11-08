namespace JourneyMonitor.Core.Options;

/// <summary>
/// Demo mode (section "Demo"): only the demo stack turns it on, so the portal can show a
/// permanent banner saying every number on screen is synthetic. Off by default, and when off the
/// portal renders exactly as before.
/// </summary>
public sealed class DemoOptions
{
    public const string SectionName = "Demo";

    public const string DefaultLabel = "DEMO — synthetic data";

    public bool Enabled { get; set; }

    public string? Label { get; set; }

    public string ResolvedLabel => string.IsNullOrWhiteSpace(Label) ? DefaultLabel : Label.Trim();

    /// <summary>The body of <c>GET /api/demo-mode</c>.</summary>
    public DemoModeInfo ToInfo() => new(Enabled, ResolvedLabel);
}

/// <summary>Serialized with web defaults as <c>{"enabled":true,"label":"DEMO — synthetic data"}</c>.</summary>
public sealed record DemoModeInfo(bool Enabled, string Label);
