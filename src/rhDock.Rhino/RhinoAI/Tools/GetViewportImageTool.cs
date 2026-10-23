// Adapted from McNeel RhinoAI, MIT; see UPSTREAM.md.
using System;
using System.Linq;
using System.Text.Json;
using System.Collections;
using System.ComponentModel;
using System.Collections.Generic;

using static rhDock.RhinoAI.Tools.ToolResult;
using rhDock.RhinoAI.Server;

using Rhino;

using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

using global::Rhino.Display;
using global::Rhino.DocObjects;
using global::Rhino.Geometry;

namespace rhDock.RhinoAI.Tools;

[McpServerToolType]
internal static class GetViewportImageTool
{

    [McpServerTool("get_viewport_image", "Capture Viewport Image", false, false)]
    [Description("Capture the active Rhino viewport as JPG, or PNG with format png. Returns the image plus a JSON metadata block describing the resulting camera, display mode, framed scene bounds, and on-screen object count — use the metadata to diagnose empty/off-screen captures without re-shooting.")]
    public static IToolResult GetViewportImage(
        RhinoDoc doc,
        [Description("Image width pixels (default 960). For your own inspection keep the default, or go up to 1568 on the longer side when detail is unclear; models scale larger images down anyway. Up to 4096 only when the user asks for a bigger image to view or keep.")] int width = 960,
        [Description("Image height pixels (default 540). Up to 1568 on the longer side for your own inspection; up to 4096 only when the user asks for a bigger image.")] int height = 540,
        [Description("Standard view: top, bottom, left, right, front, back, perspective")] string? view = null,
        [Description("Display mode by English name: Wireframe, Shaded, Rendered, Ghosted, X-Ray, Technical, Artistic, Pen, Monochrome, Arctic, Raytraced")] string? displayMode = null,
        [Description("Camera position {x,y,z}")] Vector3d? cameraLocation = null,
        [Description("Camera look-at point {x,y,z}")] Vector3d? target = null,
        [Description("Frame this bounding box (min corner). Pair with boxMax. Replaces zoom — agent supplies what to frame, tool computes how far back to stand.")] Vector3d? boxMin = null,
        [Description("Frame this bounding box (max corner). Pair with boxMin.")] Vector3d? boxMax = null,
        [Description("Magnification factor: >1 zoom in, 0<x<1 zoom out. Applied after boxMin/boxMax if both supplied.")] double? zoom = null,
        [Description("jpg (default) or png. Use png for line work, annotations, or technical display modes, where JPEG blurs thin lines, and for an image the user will keep.")] string format = "jpg",
        [Description("PNG only: a transparent background instead of the viewport background, for placing the image in layouts.")] bool transparent = false)
    {
        if (doc.IsHeadless)
            return Failure(ToolError.RH_Doc_Headless);

        if (width <= 0)
            return Failure(ToolError.BadArgument, $"{nameof(width)} was {width}", $"Pass a {nameof(width)} greater than 0.");

        if (height <= 0)
            return Failure(ToolError.BadArgument, $"{nameof(height)} was {height}", $"Pass a {nameof(height)} greater than 0.");

        if (zoom is <= 0)
            return Failure(ToolError.BadArgument, $"{nameof(zoom)} was {zoom}", $"Pass a {nameof(zoom)} greater than 0: above 1 zooms in, below 1 zooms out.");

        bool? chosen = format.Trim().TrimStart('.').ToLowerInvariant() switch { "png" => true, "jpg" or "jpeg" => false, _ => null };
        if (chosen is not { } png)
            return Failure(ToolError.BadArgument, $"Unknown format {format}", "Pass jpg or png.");
        if (transparent && !png)
            return Failure(ToolError.BadArgument, "transparent needs format png", "Pass format: png with transparent: true.");

        Coercions coerced = new();
        // rhDock: the cap is 4096 instead of 1280 x 720, so a user's request for a larger image can be met; the descriptions keep
        // the agent's own inspection at 960 x 540 by default and at most 1568 on the longer side, since every image it reads costs context.
        width = coerced.Clamp(nameof(width), width, 1, 4096);
        height = coerced.Clamp(nameof(height), height, 1, 4096);

        RhinoView? activeView = doc.Views.ActiveView;
        if (activeView is null)
            return Failure(ToolError.RH_View_NotFound, guidance: "Ask the user to open a viewport");

        Bitmap? bitmap = null;
        CaptureMetadata? meta = null;

        RhinoViewport vp = activeView.ActiveViewport;

        try
        {
            if (!String.IsNullOrEmpty(view))
            {
                DefinedViewportProjection proj = ParseProjection(view);
                if (proj == DefinedViewportProjection.None)
                    return Failure(ToolError.BadArgument, $"Unknown view: {view}");

                vp.SetProjection(proj, null, true);
            }

            if (!String.IsNullOrEmpty(displayMode))
            {
                DisplayModeDescription? mode = FindDisplayMode(displayMode);
                if (mode is null)
                    return Failure(ToolError.BadArgument, $"Unknown display mode: {displayMode}");

                vp.DisplayMode = mode;
            }

            if (cameraLocation is not null)
                vp.SetCameraLocation((Point3d)cameraLocation, false);

            if (target is not null)
                vp.SetCameraTarget((Point3d)target, false);

            if (boxMin is null != boxMax is null)
                return Failure(ToolError.BadArgument, "boxMin and boxMax must be supplied together", "Pass both corners, or neither");

            if (boxMin is not null && boxMax is not null)
            {
                BoundingBox bb = new((Point3d)boxMin, (Point3d)boxMax);
                if (!bb.IsValid)
                    return Failure(ToolError.BadArgument, "boxMin/boxMax do not form a valid bounding box.");

                vp.ZoomBoundingBox(bb);
            }

            if (zoom.HasValue)
            {
                vp.Magnify(zoom.Value, true);

                double magnified = vp.Camera35mmLensLength;
                double bounded = coerced.Clamp(
                    $"the lens length {nameof(zoom)} produced",
                    magnified,
                    SetCameraTool.LensMinimum,
                    SetCameraTool.LensMaximum);

                if (bounded != magnified)
                    vp.Camera35mmLensLength = bounded;
            }

            activeView.Redraw();
            // rhDock: Redraw only queues a draw, and ViewCapture captured before it ran, so a display mode set above came out in the
            // previous mode (the second call was right). Let Rhino draw the view in its new state before capturing.
            RhinoApp.Wait();

            meta = GatherMetadata(activeView, width, height);

            // Claude likes to "see" GH script creations, which is made impossible with this check
            // Whilst a GH is open or has components could be checked, other 3rd party plugins may also create phantom objects
            // if (meta.VisibleObjectCount == 0)
            // {
            //     return Failure(
            //         ToolError.RH_Nothing_Visible,
            //         ContentBlock.CreateText(SerializeResult(meta)),
            //         "No document objects intersect the view frustum",
            //         "Camera/target may be off the model. See metadata.scene.boundingBox for where geometry actually lives.");
            // }

            // rhDock: ViewCapture instead of RhinoView.CaptureToBitmap(Size), which redraws a Raytraced (realtime) view from zero
            // samples and returns only the overlays. For a realtime view, capture as many passes as the viewport has rendered so far.
            ViewCapture capture = new() { Width = width, Height = height, TransparentBackground = transparent };
            if (activeView.RealtimeDisplayMode is { } realtime)
            {
                capture.RealtimeRenderPasses = Math.Max(1, realtime.HudLastRenderedPass());
                meta.RealtimePasses = capture.RealtimeRenderPasses;
            }
            bitmap = capture.CaptureToBitmap(activeView);
        }
        catch (Exception ex)
        {
            return Failure(ToolError.Exception, ContentBlock.CreateText(SerializeResult(meta)), $"Capture failed: {ex.Message}");
        }

        if (bitmap is null)
            return Failure(ToolError.Failed, ContentBlock.CreateText(SerializeResult(meta)), "Could not capture image");

        using MemoryStream ms = new();
        bitmap.Save(ms, png ? ImageFormat.Png : ImageFormat.Jpeg);

        return Success(
            [
                ContentBlock.CreateText(SerializeResult(meta)),
                ContentBlock.CreateImage(ms.ToArray(), png ? "image/png" : "image/jpeg"),
            ],
            coerced.Guidance);
    }

    private sealed class CaptureMetadata(GetContextTool.ViewportSummary viewport)
    {
        public GetContextTool.ViewportSummary Viewport { get; } = viewport;
        public int ImageWidth { get; set; }
        public int ImageHeight { get; set; }
        public BoundingBox SceneBoundingBox { get; set; } = BoundingBox.Empty;
        public int VisibleObjectCount { get; set; }
        public int TotalObjectCount { get; set; }
        public int? RealtimePasses { get; set; }
    }

    private static CaptureMetadata GatherMetadata(RhinoView activeView, int width, int height)
    {
        RhinoViewport vp = activeView.ActiveViewport;
        RhinoDoc doc = activeView.Document;

        CaptureMetadata meta = new(GetContextTool.SummarizeViewport(vp))
        {
            ImageWidth = width,
            ImageHeight = height,
        };

        BoundingBox sceneBox = BoundingBox.Empty;
        int total = 0;
        int visible = 0;
        DisplayPipeline pipeline = activeView.DisplayPipeline;

        ObjectEnumeratorSettings settings = new()
        {
            ActiveObjects = true,
            HiddenObjects = false,
            LockedObjects = true,
            DeletedObjects = false,
            VisibleFilter = true,
        };

        foreach (RhinoObject obj in doc.Objects.GetObjectList(settings))
        {
            BoundingBox bb = obj.Geometry.GetBoundingBox(true);
            if (!bb.IsValid)
                continue;
            total++;
            sceneBox.Union(bb);
            if (pipeline is not null && pipeline.IsVisible(bb))
                visible++;
        }

        meta.SceneBoundingBox = sceneBox;
        meta.TotalObjectCount = total;
        meta.VisibleObjectCount = visible;
        return meta;
    }

    private static string SerializeResult(CaptureMetadata? meta)
    {
        var payload = new
        {
            metadata = meta is null ? null : new
            {
                viewport = new
                {
                    name = meta.Viewport.Name,
                    displayMode = meta.Viewport.DisplayMode,
                    projection = meta.Viewport.Camera.Projection,
                    width = meta.ImageWidth,
                    height = meta.ImageHeight,
                    realtimePasses = meta.RealtimePasses,
                },
                camera = new
                {
                    location = meta.Viewport.Camera.Location,
                    target = meta.Viewport.Camera.Target,
                    up = meta.Viewport.Camera.Up,
                    lensLength = meta.Viewport.Camera.LensLength,
                },
                scene = new
                {
                    boundingBox = meta.SceneBoundingBox.IsValid ? new
                    {
                        min = GetContextTool.XYZ(meta.SceneBoundingBox.Min),
                        max = GetContextTool.XYZ(meta.SceneBoundingBox.Max),
                    } : null,
                    visibleObjectCount = meta.VisibleObjectCount,
                    totalObjectCount = meta.TotalObjectCount,
                },
            },
        };
        return JsonSerializer.Serialize(payload);
    }

    private static DefinedViewportProjection ParseProjection(string s) => s.ToLowerInvariant() switch
    {
        "top" => DefinedViewportProjection.Top,
        "bottom" => DefinedViewportProjection.Bottom,
        "left" => DefinedViewportProjection.Left,
        "right" => DefinedViewportProjection.Right,
        "front" => DefinedViewportProjection.Front,
        "back" => DefinedViewportProjection.Back,
        "perspective" => DefinedViewportProjection.Perspective,
        _ => DefinedViewportProjection.None,
    };

    private static DisplayModeDescription? FindDisplayMode(string name)
    {
        foreach (DisplayModeDescription mode in DisplayModeDescription.GetDisplayModes())
        {
            if (string.Equals(mode.EnglishName, name, StringComparison.OrdinalIgnoreCase))
                return mode;
        }
        return null;
    }
}
