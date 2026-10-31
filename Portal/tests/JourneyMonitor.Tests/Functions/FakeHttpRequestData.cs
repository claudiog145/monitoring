using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace JourneyMonitor.Tests.Functions;

/// <summary>
/// Minimal in-process HttpRequestData/HttpResponseData so a Function's JSON contract can be
/// asserted without a host. The Web proxy and the UI are built against that JSON, so the shape -
/// camelCase keys included - is worth pinning in a test rather than discovering at runtime.
///
/// Everything the Functions under test never touch (cookies, binding and retry context, features)
/// throws instead of returning a plausible-looking empty value: a test that starts depending on
/// one of them should fail loudly rather than pass against a fiction.
/// </summary>
internal sealed class FakeFunctionContext : FunctionContext
{
    public override string InvocationId { get; } = Guid.NewGuid().ToString();

    public override string FunctionId { get; } = "test-function";

    public override TraceContext TraceContext => throw new NotSupportedException();

    public override BindingContext BindingContext => throw new NotSupportedException();

    public override RetryContext RetryContext => throw new NotSupportedException();

    public override IServiceProvider InstanceServices { get; set; } = null!;

    public override FunctionDefinition FunctionDefinition => throw new NotSupportedException();

    public override IDictionary<object, object> Items { get; set; } = new Dictionary<object, object>();

    public override IInvocationFeatures Features => throw new NotSupportedException();
}

internal sealed class FakeHttpRequestData : HttpRequestData
{
    public FakeHttpRequestData(string url) : base(new FakeFunctionContext())
    {
        Url = new Uri(url);
    }

    public override Stream Body { get; } = new MemoryStream();

    public override HttpHeadersCollection Headers { get; } = new();

    public override IReadOnlyCollection<IHttpCookie> Cookies => throw new NotSupportedException();

    public override Uri Url { get; }

    public override IEnumerable<ClaimsIdentity> Identities => [];

    public override string Method => "GET";

    public override HttpResponseData CreateResponse() => new FakeHttpResponseData(FunctionContext);

    public FakeHttpRequestData WithHeader(string name, string value)
    {
        Headers.Add(name, value);
        return this;
    }
}

internal sealed class FakeHttpResponseData(FunctionContext context) : HttpResponseData(context)
{
    public override HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;

    public override HttpHeadersCollection Headers { get; set; } = new();

    public override Stream Body { get; set; } = new MemoryStream();

    public override HttpCookies Cookies => throw new NotSupportedException();
}

internal static class FakeHttpExtensions
{
    /// <summary>Reads the serialized body back as JSON, which is exactly what the Web proxy receives.</summary>
    public static JsonNode ReadJson(this HttpResponseData response)
    {
        response.Body.Position = 0;
        using var reader = new StreamReader(response.Body, Encoding.UTF8, leaveOpen: true);
        return JsonNode.Parse(reader.ReadToEnd())
            ?? throw new InvalidOperationException("The response body was not JSON.");
    }
}
