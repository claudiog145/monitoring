using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Infrastructure.Sql;

/// <summary>
/// SQL float cannot hold NaN or ±Infinity (and the JSON batch serializer refuses them), so both the
/// SQL and the in-memory KPI stores reject such values up front with the same ArgumentException.
/// </summary>
internal static class KpiValueGuard
{
    internal static void EnsureFinite(IReadOnlyList<KpiBucket> buckets, string paramName)
    {
        foreach (var b in buckets)
        {
            if (!IsFinite(b.P50) || !IsFinite(b.P95) || !IsFinite(b.Mean) || !IsFinite(b.StdDev) || !IsFinite(b.Value))
            {
                throw new ArgumentException(
                    $"KpiBucket {b.KpiDefinitionId}@{b.BucketStart:o} has a NaN or infinite statistic.", paramName);
            }
        }
    }

    internal static void EnsureFinite(IReadOnlyList<KpiSample> samples, string paramName)
    {
        foreach (var s in samples)
        {
            if (!double.IsFinite(s.Value))
            {
                throw new ArgumentException(
                    $"KpiSample {s.KpiDefinitionId}@{s.Timestamp:o} has a NaN or infinite value.", paramName);
            }
        }
    }

    private static bool IsFinite(double? value) => value is not { } v || double.IsFinite(v);

    /// <summary>Width of the EntityKey columns (nvarchar(256)); OPENJSON would silently truncate a longer key.</summary>
    internal const int MaxEntityKeyLength = 256;

    /// <summary>
    /// Stored/queried form of an entity key: null is the domain aggregate "", surrounding whitespace is
    /// dropped (SQL ignores trailing spaces when comparing, so untrimmed keys would collide in MERGE/PKs).
    /// </summary>
    internal static string NormalizeEntityKey(string? entityKey) => (entityKey ?? "").Trim();

    internal static void EnsureEntityKeys(IEnumerable<string?> entityKeys, string paramName)
    {
        foreach (var key in entityKeys)
        {
            EnsureEntityKey(key, paramName);
        }
    }

    internal static void EnsureEntityKey(string? entityKey, string paramName)
    {
        if (entityKey is null)
        {
            throw new ArgumentException("EntityKey must not be null; use \"\" for the domain aggregate.", paramName);
        }
        if (entityKey.Length > MaxEntityKeyLength)
        {
            throw new ArgumentException($"EntityKey is longer than {MaxEntityKeyLength} characters.", paramName);
        }
    }
}
