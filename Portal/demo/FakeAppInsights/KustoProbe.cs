namespace FakeAppInsights;

/// <summary>Checks that the Kustainer engine answers a management command (contract v5 §3.2).</summary>
public sealed class KustoProbe(HttpClient http)
{
    public async Task<bool> IsReachableAsync(string? kustoUrl, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(kustoUrl)) return false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            using var response = await http.PostAsJsonAsync(
                new Uri(new Uri(kustoUrl.TrimEnd('/') + "/"), "v1/rest/mgmt"),
                new { db = "NetDefaultDB", csl = ".show version" },
                timeout.Token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            return false;
        }
    }
}
