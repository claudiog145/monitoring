using System.Text.Json;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Company;
using Microsoft.Extensions.Logging;

namespace JourneyMonitor.Core.Monitoring.Privacy;

/// <summary>
/// The pieces every agent needs to apply contract v4 §5 the same way Triage and the Inspector do:
/// resolve the profile that governs a prompt, redact text and JSON evidence, and describe what was
/// hidden as pattern names and counts only. The per-profile rule itself lives in
/// <see cref="EvidenceRedactor.RedactionRequired"/>; nothing here decides policy.
/// </summary>
public static class PromptPrivacy
{
    /// <summary>The key used when the governing company is unknown. Never the incumbent, so it always redacts.</summary>
    public const string UnresolvedProfileKey = "(unresolved)";

    /// <summary>A profile for "the company could not be determined": closed by default, like any non-incumbent key.</summary>
    public static CompanyProfile ClosedProfile() => EvidenceRedactor.PlaceholderProfile(UnresolvedProfileKey);

    /// <summary>
    /// The profile whose Privacy settings govern a prompt. A store that is missing, fails or has no
    /// row for the key falls back to <see cref="EvidenceRedactor.PlaceholderProfile"/>, which is
    /// closed for every key but the incumbent - a lookup failure never sends raw evidence.
    /// </summary>
    public static async Task<CompanyProfile> ResolveProfileAsync(
        ICompanyProfileStore? profiles, string? profileKey, ILogger logger, string agent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(logger);
        var key = string.IsNullOrWhiteSpace(profileKey) ? KpiProfiles.Default : profileKey.Trim();
        if (profiles is not null)
        {
            try
            {
                if (await profiles.GetAsync(key, ct) is { } stored) return stored;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[{Agent}] could not read profile {ProfileKey}; redacting with the closed default.", agent, key);
            }
        }
        return EvidenceRedactor.PlaceholderProfile(key);
    }

    /// <summary>Redacts <paramref name="text"/> for <paramref name="profile"/> and adds what was hidden into <paramref name="counts"/>.</summary>
    public static string Redact(IEvidenceRedactor redactor, string text, CompanyProfile profile, IDictionary<string, int> counts)
    {
        ArgumentNullException.ThrowIfNull(redactor);
        var result = redactor.Redact(text, profile);
        EvidenceRedactor.Accumulate(counts, result.CountsByPattern);
        return result.Text;
    }

    /// <summary>"name=count, ..." in ordinal order. Never carries a redacted value.</summary>
    public static string Describe(IEnumerable<KeyValuePair<string, int>> counts) =>
        string.Join(", ", counts.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => $"{c.Key}={c.Value}"));

    /// <summary>
    /// A copy of <paramref name="element"/> with personal data removed, for evidence that is
    /// persisted rather than prompted. Works value by value so the result is still valid JSON:
    /// every string goes through the redactor, a property named like one of the profile's
    /// <c>PiiDimensions</c> has its whole value replaced, and a number the redactor would mask
    /// (a Luhn-valid card, say) becomes the token string. Returns the element untouched when the
    /// profile does not require redaction.
    ///
    /// Known limitation: property NAMES are kept as they are. An object keyed by a personal value
    /// (an email or member id used as a dictionary key) would keep that key; no current evidence
    /// provider emits that shape, and rewriting names would break the structure readers depend on.
    /// </summary>
    public static JsonElement RedactJson(IEvidenceRedactor redactor, JsonElement element, CompanyProfile profile, IDictionary<string, int> counts)
    {
        ArgumentNullException.ThrowIfNull(redactor);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(counts);
        if (!EvidenceRedactor.RedactionRequired(profile)) return element;

        var dimensions = (profile.Privacy?.PiiDimensions ?? [])
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Select(d => d.Trim())
            .ToList();

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            Write(writer, element, redactor, profile, dimensions, counts);
        }
        using var document = JsonDocument.Parse(buffer.ToArray());
        return document.RootElement.Clone();
    }

    private static void Write(
        Utf8JsonWriter writer, JsonElement element, IEvidenceRedactor redactor, CompanyProfile profile,
        IReadOnlyList<string> dimensions, IDictionary<string, int> counts)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    var dimension = dimensions.FirstOrDefault(d => string.Equals(d, property.Name.Trim(), StringComparison.OrdinalIgnoreCase));
                    if (dimension is not null && property.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                    {
                        writer.WriteStringValue(EvidenceRedactor.PiiToken);
                        EvidenceRedactor.Accumulate(counts, new Dictionary<string, int> { [EvidenceRedactor.Keys.Dimension(dimension)] = 1 });
                        continue;
                    }
                    Write(writer, property.Value, redactor, profile, dimensions, counts);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray()) Write(writer, item, redactor, profile, dimensions, counts);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(Redact(redactor, element.GetString() ?? "", profile, counts));
                break;
            case JsonValueKind.Number:
                var raw = element.GetRawText();
                var redacted = Redact(redactor, raw, profile, counts);
                if (string.Equals(raw, redacted, StringComparison.Ordinal)) writer.WriteRawValue(raw, skipInputValidation: true);
                else writer.WriteStringValue(redacted);
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}
