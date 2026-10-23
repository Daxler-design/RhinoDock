using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace rhDock.Rhino;

/// <summary>
/// Small previews of Agent tab images: the conversation shows these, and the full image is sent only when the user opens it, so a
/// large capture does not slow the conversation. Kept out of AgentHost so its portable checks need no imaging library.
/// </summary>
internal static class ImageThumbnail
{
    private const int Longest = 400;
    // ponytail: cleared when full; an LRU would keep hot entries if conversations hold many images.
    private static readonly ConcurrentDictionary<string, string> cache = new();

    /// <summary>The image as a data URL at most 400 pixels on its longer side; the image itself when already small or not decodable.</summary>
    public static string? Of(string? dataUrl)
    {
        if (dataUrl is null) return null;
        var key = dataUrl.Length + ":" + dataUrl.GetHashCode();
        if (cache.TryGetValue(key, out var known)) return known;
        var thumb = Make(dataUrl);
        if (cache.Count > 200) cache.Clear();
        cache[key] = thumb;
        return thumb;
    }

    private static string Make(string dataUrl)
    {
        var comma = dataUrl.IndexOf(";base64,", StringComparison.Ordinal);
        if (!dataUrl.StartsWith("data:image/", StringComparison.Ordinal) || comma < 0) return dataUrl;
        try
        {
            using var input = new MemoryStream(Convert.FromBase64String(dataUrl[(comma + 8)..]));
            using var image = Image.FromStream(input);
            var scale = (double)Longest / Math.Max(image.Width, image.Height);
            if (scale >= 1) return dataUrl;
            using var small = new Bitmap(Math.Max(1, (int)(image.Width * scale)), Math.Max(1, (int)(image.Height * scale)));
            using (var g = Graphics.FromImage(small))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(image, 0, 0, small.Width, small.Height);
            }
            // PNG keeps a transparent background; everything else becomes a small JPEG.
            var png = dataUrl.StartsWith("data:image/png", StringComparison.Ordinal);
            using var output = new MemoryStream();
            small.Save(output, png ? ImageFormat.Png : ImageFormat.Jpeg);
            return (png ? "data:image/png;base64," : "data:image/jpeg;base64,") + Convert.ToBase64String(output.ToArray());
        }
        catch (Exception ex)
        {
            HostLog.Write("thumbnail not made: " + ex.GetType().Name);
            return dataUrl;
        }
    }
}
