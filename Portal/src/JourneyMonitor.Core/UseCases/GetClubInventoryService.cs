using JourneyMonitor.Core.Abstractions;

namespace JourneyMonitor.Core.UseCases;

/// <summary>
/// Use case behind GET /api/club-inventory. Returns every row of <c>brand.ClubInventory</c>
/// for the Club Map tab. Filtering stays in the UI for now (a few thousand rows).
/// </summary>
public sealed class GetClubInventoryService(IClubInventoryStore store)
{
    public const string DatabaseSource = "database";

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
}
