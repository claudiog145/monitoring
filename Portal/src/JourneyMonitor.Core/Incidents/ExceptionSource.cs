namespace JourneyMonitor.Core.Incidents;

/// <summary>
/// Where an exception was thrown. Application Insights <c>client_Type</c> is Browser for the
/// JavaScript SDK and PC for a server process. Stored values are only these two.
/// </summary>
public static class ExceptionSource
{
    public const string Server = "Server";
    public const string Browser = "Browser";

    /// <summary>
    /// Maps an Application Insights <c>client_Type</c>, or an already stored source, to
    /// <see cref="Browser"/> or <see cref="Server"/>. Anything that is not Browser is Server.
    /// </summary>
    public static string Normalize(string? clientTypeOrSource) =>
        string.Equals(clientTypeOrSource?.Trim(), Browser, StringComparison.OrdinalIgnoreCase)
            ? Browser
            : Server;
}
