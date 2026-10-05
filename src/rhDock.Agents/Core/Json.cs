using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace rhDock.Agents;

/// <summary>JSON accessors the adapters share. Each returns null (or nothing) for a missing value or one of another type.</summary>
internal static class Json
{
    public static string? Str(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    public static double? Num(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
    public static bool Bool(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
    public static JsonElement Obj(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : default;
    public static IEnumerable<JsonElement> Arr(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];
    /// <summary>The first string among the names, for inputs that spell the same field differently across agents.</summary>
    public static string? First(JsonElement e, params string[] names) => names.Select(n => Str(e, n)).FirstOrDefault(v => v is not null);
}
