using System.Globalization;
using System.Text.Json;

namespace rhDock.Core;

/// <summary>Strict JSON values at the host-tools boundary; never constructs Script-defined types.</summary>
public static class HostInput
{
    public static object? Parse(string element, PortAccess access, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (access == PortAccess.Tree) throw new ArgumentException("Tree inputs are not supported.");
        object Scalar(JsonElement item) => element switch
        {
            "System.Double" when item.ValueKind == JsonValueKind.Number && double.IsFinite(item.GetDouble()) => item.GetDouble(),
            "System.Int32" when item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var number) => number,
            "System.Boolean" when item.ValueKind is JsonValueKind.True or JsonValueKind.False => item.GetBoolean(),
            "System.String" when item.ValueKind == JsonValueKind.String => item.GetString()!,
            "System.Guid" when item.ValueKind == JsonValueKind.String && Guid.TryParse(item.GetString(), out var id) => id,
            "System.DateTime" when item.ValueKind == JsonValueKind.String && DateTime.TryParseExact(item.GetString(), "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date) => date,
            "System.Drawing.Color" or "Rhino.Display.Color4f" when item.ValueKind == JsonValueKind.String && ColorText.Parse(item.GetString()!) is { } color => color,
            _ => throw new ArgumentException("Value must match " + element + ". Geometry inputs use rhdock_set_picks or rhdock_request_pick.")
        };
        if (access == PortAccess.Item) return Scalar(value);
        if (value.ValueKind != JsonValueKind.Array) throw new ArgumentException("A list input requires a JSON array.");
        var type = element switch
        {
            "System.Double" => typeof(double), "System.Int32" => typeof(int), "System.Boolean" => typeof(bool),
            "System.String" => typeof(string), "System.Guid" => typeof(Guid), "System.DateTime" => typeof(DateTime),
            "System.Drawing.Color" or "Rhino.Display.Color4f" => typeof(System.Drawing.Color),
            _ => throw new ArgumentException("Unsupported input type: " + element)
        };
        var array = Array.CreateInstance(type, value.GetArrayLength());
        var at = 0;
        foreach (var item in value.EnumerateArray()) array.SetValue(Scalar(item), at++);
        return array;
    }
}
