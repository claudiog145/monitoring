using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Company;

namespace JourneyMonitor.Core.UseCases;

/// <summary>
/// Use case behind GET /api/club-inventory. Returns every row of <c>brand.ClubInventory</c>
/// for the Application Map tab. Filtering stays in the UI for now (a few thousand rows).
/// </summary>
public sealed class GetClubInventoryService(IClubInventoryStore store)
{
    public const string DatabaseSource = "database";
    public const string NotApplicableSource = "not-applicable";

    /// <summary>
    /// Contract v6 §5: brand.ClubInventory is the incumbent's club catalogue, so another company gets an
    /// empty answer with <see cref="ClubInventoryResult.Applicable"/> false instead of the incumbent's clubs.
    /// </summary>
    public async Task<ClubInventoryResult> GetAsync(ProfileReadScope scope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (!scope.IncludesIncumbent)
        {
            return new ClubInventoryResult
            {
                Source = NotApplicableSource,
                Applicable = false,
                ProfileKey = scope.Label,
                Warning = "The club inventory only exists for the incumbent company.",
                Clubs = [],
            };
        }

        var result = await GetAsync(ct).ConfigureAwait(false);
        return new ClubInventoryResult
        {
            Source = result.Source,
            RowCount = result.RowCount,
            Warning = result.Warning,
            Clubs = result.Clubs,
            Applicable = true,
            ProfileKey = scope.Label,
        };
    }

    public async Task<ClubInventoryResult> GetAsync(CancellationToken ct)
    {
        if (!store.IsConfigured)
        {
            return new ClubInventoryResult
            {
                Source = "none",
                Warning = "Journey store database is not configured.",
                Clubs = [],
            };
        }

        try
        {
            var rows = await store.GetAllAsync(ct);
            return new ClubInventoryResult
            {
                Source = DatabaseSource,
                RowCount = rows.Count,
                Clubs = rows,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ClubInventoryResult
            {
                Source = "error",
                Warning = "Could not read brand.ClubInventory. Apply CreateClubInventory.sql and the seed, then retry.",
                Clubs = [],
            };
        }
    }
}

public sealed class ClubInventoryResult
{
    public string Source { get; init; } = "none";

    public int RowCount { get; init; }

    public string? Warning { get; init; }

    public IReadOnlyList<ClubInventoryRow> Clubs { get; init; } = [];

    /// <summary>Contract v6 §5: false when the requested company has no club inventory (only the incumbent has one).</summary>
    public bool Applicable { get; init; } = true;

    /// <summary>The company the answer is for ("*" = every company); null when the caller did not scope it.</summary>
    public string? ProfileKey { get; init; }
}
