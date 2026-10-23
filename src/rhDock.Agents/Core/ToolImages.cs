using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using static rhDock.Agents.Json;

namespace rhDock.Agents;

/// <summary>
/// Images a tool returned, read out of the content blocks every adapter sees in a slightly different shape:
/// <c>{type:image, data, mimeType}</c> (MCP, ACP, Pi), <c>{type:image, source:{media_type, data}}</c> (Claude),
/// and <c>{type:file, mime, url:"data:..."}</c> (OpenCode). Only raster images with embedded data are shown;
/// a bare file path is not read here.
/// </summary>
internal static class ToolImages
{
    // ponytail: a fixed per-image cap; a total budget per turn would suit many large captures better.
    private const int MaxBase64 = 6 * 1024 * 1024;
    private static readonly string[] Allowed = ["image/png", "image/jpeg", "image/gif", "image/webp"];

    public static IReadOnlyList<AgentImage>? In(IEnumerable<JsonElement> blocks, string name)
    {
        var found = blocks.Select(b => From(b, name)).OfType<AgentImage>().Take(8).ToArray();
        return found.Length > 0 ? found : null;
    }

    public static AgentImage? From(JsonElement block, string name)
    {
        string? mime, data;
        var type = Str(block, "type");
        if (type == "image")
        {
            var source = Obj(block, "source");
            data = Str(block, "data") ?? Str(source, "data");
            mime = First(block, "mimeType", "mime_type", "mime") ?? First(source, "media_type", "mimeType");
        }
        else if (type == "file" && Str(block, "url") is { } url && url.StartsWith("data:", System.StringComparison.OrdinalIgnoreCase)
            && url.IndexOf(";base64,", System.StringComparison.Ordinal) is var at and > 5)
        {
            mime = url[5..at];
            data = url[(at + 8)..];
        }
        else return null;
        if (data is null || data.Length > MaxBase64 || mime is null || !Allowed.Contains(mime.ToLowerInvariant())) return null;
        return new AgentImage(mime.ToLowerInvariant(), data, name);
    }
}
