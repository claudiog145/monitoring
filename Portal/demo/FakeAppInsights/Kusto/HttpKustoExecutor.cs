using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace FakeAppInsights.Kusto;

/// <summary>Talks to Kustainer over its REST v1 API (<c>/v1/rest/query</c>, <c>/v1/rest/mgmt</c>); no auth.</summary>
public sealed class HttpKustoExecutor(HttpClient http, IOptions<FakeAppInsightsOptions> options) : IKustoExecutor
{
    public Task<IReadOnlyList<KustoTable>> QueryAsync(string database, string csl, CancellationToken cancellationToken) =>
        SendAsync("v1/rest/query", database, csl, cancellationToken);

    public Task<IReadOnlyList<KustoTable>> ManagementAsync(string database, string csl, CancellationToken cancellationToken) =>
        SendAsync("v1/rest/mgmt", database, csl, cancellationToken);

    private async Task<IReadOnlyList<KustoTable>> SendAsync(string path, string database, string csl, CancellationToken cancellationToken)
    {
        var baseUrl = options.Value.KustoUrl;
        if (string.IsNullOrWhiteSpace(baseUrl)) throw new KustoUnavailableException("FakeAppInsights:KustoUrl is not configured.");

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), path))
        {
            Content = JsonContent.Create(new { db = database, csl }),
        };
        // Without it Kustainer answers errors as plain text with no detail.
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new KustoUnavailableException($"The Kusto engine at {baseUrl} is unreachable: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new KustoUnavailableException("The Kusto engine did not answer in time.", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                var (message, code) = KustoV1Response.ReadError(body);
                if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound)
                    throw new KustoQueryException(message, code);
                throw new KustoUnavailableException($"The Kusto engine failed with {(int)response.StatusCode}: {message}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            return KustoV1Response.Parse(document.RootElement);
        }
    }
}
