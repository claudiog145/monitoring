namespace FakeAppInsights.Rewrite;

/// <summary>Base for every reason a query cannot be pointed at the demo engine. Maps to HTTP 400.</summary>
public abstract class KqlRewriteException(string message) : Exception(message);

/// <summary>The query uses a construct the demo does not serve, e.g. <c>workspace()</c>.</summary>
public sealed class UnsupportedKqlException(string message) : KqlRewriteException(message);

/// <summary>An <c>app('…')</c> reference that names no demo application.</summary>
public sealed class UnknownApplicationException(string application)
    : KqlRewriteException($"Unknown application '{application}'.")
{
    public string Application { get; } = application;
}
