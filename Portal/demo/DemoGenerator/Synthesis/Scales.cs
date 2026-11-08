namespace DemoGenerator.Synthesis;

/// <summary>Volume presets (decision D3): <c>laptop</c> is the archetype as written.</summary>
public static class Scales
{
    public const string Tiny = "tiny";
    public const string Laptop = "laptop";
    public const string Showcase = "showcase";

    public static readonly IReadOnlyList<string> All = [Tiny, Laptop, Showcase];

    public static bool TryGetFactor(string? scale, out double factor)
    {
        switch (scale?.Trim().ToLowerInvariant())
        {
            case Tiny: factor = 0.01; return true;
            case Laptop: factor = 1.0; return true;
            case Showcase: factor = 4.0; return true;
            default: factor = 0; return false;
        }
    }

    public static double Factor(string scale) =>
        TryGetFactor(scale, out var factor) ? factor : throw new ArgumentException($"Unknown scale '{scale}'. Use tiny, laptop or showcase.", nameof(scale));
}
