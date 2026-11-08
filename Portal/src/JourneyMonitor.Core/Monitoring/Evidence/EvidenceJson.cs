using System.Text.Json;
using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Core.Monitoring.Evidence;

/// <summary>Serializes evidence payloads with the same options as the triage result, so the stored JSON is camelCase end to end.</summary>
public static class EvidenceJson
{
    public static JsonElement ToElement<T>(T value) => JsonSerializer.SerializeToElement(value, TriageJson.Options);
}
