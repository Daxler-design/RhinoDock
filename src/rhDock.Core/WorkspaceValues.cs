using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace rhDock.Core;

public sealed record SavedScalar(string Kind, string? Value)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string[]? Items { get; init; }

    public static SavedScalar? Capture(object? value) => value switch
    {
        null => new("null", null),
        string s => new("string", s),
        bool b => new("bool", b ? "true" : "false"),
        int i => new("int", i.ToString(CultureInfo.InvariantCulture)),
        double d when double.IsFinite(d) => new("double", d.ToString("R", CultureInfo.InvariantCulture)),
        System.Drawing.Color c => new("color", c.ToArgb().ToString("X8", CultureInfo.InvariantCulture)),
        DateTime t => new("datetime", t.ToString("O", CultureInfo.InvariantCulture)),
        Guid g => new("guid", g.ToString()),
        IEnumerable<double> list when list.All(double.IsFinite) => new("double[]", null) { Items = list.Select(d => d.ToString("R", CultureInfo.InvariantCulture)).ToArray() },
        IEnumerable<int> list => new("int[]", null) { Items = list.Select(i => i.ToString(CultureInfo.InvariantCulture)).ToArray() },
        IEnumerable<bool> list => new("bool[]", null) { Items = list.Select(b => b ? "true" : "false").ToArray() },
        IEnumerable<string> list => new("string[]", null) { Items = list.ToArray() },
        IEnumerable<System.Drawing.Color> list => new("color[]", null) { Items = list.Select(c => c.ToArgb().ToString("X8", CultureInfo.InvariantCulture)).ToArray() },
        _ => null
    };

    public object? Restore() => Kind switch
    {
        "null" => null, "string" => Value, "bool" => bool.Parse(Value!),
        "int" => int.Parse(Value!, CultureInfo.InvariantCulture),
        "double" when double.TryParse(Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) => d,
        "color" => System.Drawing.Color.FromArgb(int.Parse(Value!, NumberStyles.HexNumber, CultureInfo.InvariantCulture)),
        "datetime" => DateTime.Parse(Value!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        "guid" => Guid.Parse(Value!),
        "double[]" => Items!.Select(item => double.Parse(item, NumberStyles.Float, CultureInfo.InvariantCulture)).Where(double.IsFinite).ToList(),
        "int[]" => Items!.Select(item => int.Parse(item, CultureInfo.InvariantCulture)).ToList(),
        "bool[]" => Items!.Select(bool.Parse).ToList(),
        "string[]" => Items!.ToList(),
        "color[]" => Items!.Select(item => System.Drawing.Color.FromArgb(int.Parse(item, NumberStyles.HexNumber, CultureInfo.InvariantCulture))).ToList(),
        _ => throw new InvalidDataException("Unsupported Workspace parameter: " + Kind)
    };
}

/// <summary>Panel color text: #RRGGBB, or #RRGGBBAA when not opaque (CSS order).</summary>
public static class ColorText
{
    public static System.Drawing.Color? Parse(string text)
    {
        var hex = text.Trim().TrimStart('#');
        if (hex.Length is not (6 or 8) || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var n)) return null;
        if (hex.Length == 6) n = n << 8 | 0xFF;
        return System.Drawing.Color.FromArgb((int)(n & 0xFF), (int)(n >> 24), (int)(n >> 16 & 0xFF), (int)(n >> 8 & 0xFF));
    }

    public static string Format(System.Drawing.Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}" + (c.A == 255 ? "" : c.A.ToString("X2", CultureInfo.InvariantCulture));
}

/// <summary>One picked Sub-object as text and an integer, so Core needs no RhinoCommon type (ADR 0006).</summary>
public sealed record SavedComponent(string Type, int Index);

public sealed record SavedPick(string Kind, string? ObjectId, double[]? Point, bool IsList)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string[]? Ids { get; init; }
    /// <summary>For Kind "components": one entry per <see cref="Ids"/> entry, naming the part of that object.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public SavedComponent[]? Components { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double[][]? Points { get; init; }
}

public static class AtomicJson
{
    private static JsonSerializerOptions Options => new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    public static void Write<T>(string path, T data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, data, Options); stream.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options)
        ?? throw new InvalidDataException("Empty JSON document: " + path);
}

/// <summary>Transient display settings for one geometry input or output. Null fields use the host default.</summary>
public sealed record PreviewStyle(string? Color, int? Weight, int? Size, int? Shading)
{
    public bool IsValid => (Color is null || System.Text.RegularExpressions.Regex.IsMatch(Color, "^#[0-9A-Fa-f]{6}$"))
        && Weight is null or (>= 1 and <= 32) && Size is null or (>= 1 and <= 64) && Shading is null or (>= 0 and <= 100);
}
