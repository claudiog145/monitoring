using DemoGenerator.Kusto;

namespace DemoGenerator.Runtime;

/// <summary>Kusto reachability for <c>/health</c>, probed with <c>.show version</c> and cached for a few seconds.</summary>
public sealed class KustoHealth(IKustoClient kusto, TimeProvider time)
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(10);
    private DateTimeOffset _checkedAt = DateTimeOffset.MinValue;
    private bool _reachable;

    public async Task<bool> IsReachableAsync(CancellationToken ct)
    {
        if (time.GetUtcNow() - _checkedAt < CacheFor) return _reachable;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await kusto.ExecuteCommandAsync(DemoKustoAdmin.DefaultDatabase, ".show version", timeout.Token);
            _reachable = true;
        }
        catch (Exception ex) when (ex is HttpRequestException or KustoCommandException or OperationCanceledException or TaskCanceledException)
        {
            if (ct.IsCancellationRequested) throw;
            _reachable = false;
        }
        _checkedAt = time.GetUtcNow();
        return _reachable;
    }
}
