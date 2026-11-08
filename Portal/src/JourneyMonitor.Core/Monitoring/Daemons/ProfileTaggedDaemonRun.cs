using System.Text.Json;
using System.Text.Json.Nodes;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Core.Monitoring.Daemons;

/// <summary>
/// Contract v6 §5 (MC7): every entry a daemon writes about one company's KPI, investigation or board carries
/// that company's <c>profileKey</c> in its data, so the console of another company never shows it. Wrap the
/// run once per company-owned unit of work; entries written through the wrapper are tagged even when the call
/// site forgot to put the key in its data. A key already present in the data is kept.
/// </summary>
public sealed class ProfileTaggedDaemonRun : IDaemonRun
{
    public const string ProfileKeyProperty = "profileKey";

    private readonly IDaemonRun _inner;

    public ProfileTaggedDaemonRun(IDaemonRun inner, string profileKey)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileKey);
        _inner = inner;
        ProfileKey = profileKey.Trim();
    }

    /// <summary>A tagged view of <paramref name="inner"/>, or <paramref name="inner"/> itself when there is no profile.</summary>
    public static IDaemonRun For(IDaemonRun inner, string? profileKey) =>
        string.IsNullOrWhiteSpace(profileKey) ? inner : new ProfileTaggedDaemonRun(inner, profileKey);

    public string ProfileKey { get; }
    public Guid RunId => _inner.RunId;
    public string DaemonKey => _inner.DaemonKey;

    public void Info(string kind, string message, object? data = null) => _inner.Info(kind, message, Tag(data, ProfileKey));
    public void Warning(string kind, string message, object? data = null) => _inner.Warning(kind, message, Tag(data, ProfileKey));
    public void Error(string kind, string message, object? data = null) => _inner.Error(kind, message, Tag(data, ProfileKey));
    public Task FlushAsync(CancellationToken ct = default) => _inner.FlushAsync(ct);

    /// <summary>The data as a JSON object that names <paramref name="profileKey"/>; non-object data is kept under <c>data</c>.</summary>
    public static object Tag(object? data, string profileKey)
    {
        JsonNode? node;
        try
        {
            node = data is null ? null : JsonSerializer.SerializeToNode(data, data.GetType(), TriageJson.Options);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            node = null;
        }

        var tagged = node as JsonObject ?? new JsonObject();
        if (node is not null and not JsonObject) tagged["data"] = node;
        if (tagged[ProfileKeyProperty] is not JsonValue existing
            || existing.GetValueKind() != JsonValueKind.String
            || string.IsNullOrWhiteSpace(existing.GetValue<string>()))
        {
            tagged[ProfileKeyProperty] = profileKey;
        }
        return tagged;
    }
}
