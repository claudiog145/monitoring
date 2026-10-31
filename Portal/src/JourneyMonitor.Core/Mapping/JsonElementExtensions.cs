using System.Text.Json;

namespace JourneyMonitor.Core.Mapping;

/// <summary>Small typed-getter helpers over JsonElement "bags" produced by Kusto's bag_pack (stage/call/error objects).</summary>
public static class JsonElementExtensions
{
    public static JsonElement? Prop(this JsonElement el, string name)
    {
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var value)) return value;
        return null;
    }

    public static string Str(this JsonElement el, string name, string fallback = "")
    {
        var p = el.Prop(name);
        if (p is null) return fallback;
        return p.Value.ValueKind switch
        {
            JsonValueKind.String => p.Value.GetString() ?? fallback,
            JsonValueKind.Number => p.Value.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => fallback,
        };
    }

    public static double? Dbl(this JsonElement el, string name)
    {
        var p = el.Prop(name);
        if (p is null || p.Value.ValueKind == JsonValueKind.Null) return null;
        if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetDouble(out var d)) return d;
        if (p.Value.ValueKind == JsonValueKind.String && double.TryParse(p.Value.GetString(), out var ds)) return ds;
        return null;
    }

    public static int Int(this JsonElement el, string name, int fallback = 0) => (int)Math.Round(el.Dbl(name) ?? fallback);

    public static bool Bool(this JsonElement el, string name)
    {
        var p = el.Prop(name);
        if (p is null) return false;
        return p.Value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => string.Equals(p.Value.GetString(), "true", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    /// <summary>Reads an array-or-object-of-values property the way Kusto sometimes serializes dynamic lists.</summary>
    public static List<JsonElement> ArrayOrObjectValues(this JsonElement el, string name)
    {
        var p = el.Prop(name);
        if (p is null) return [];
        if (p.Value.ValueKind == JsonValueKind.Array) return [.. p.Value.EnumerateArray()];
        if (p.Value.ValueKind == JsonValueKind.Object) return [.. p.Value.EnumerateObject().Select(x => x.Value)];
        return [];
    }
}
