using Rhino.Display;
using Rhino.Geometry;

namespace rhDock.Rhino;

public sealed class DockConduit : DisplayConduit
{
    public Preview Preview { get; set; } = Preview.Empty;
    public uint DocumentSerial { get; set; }
    private bool IsTarget(DrawEventArgs e) => e.RhinoDoc?.RuntimeSerialNumber == DocumentSerial;

    protected override void CalculateBoundingBox(CalculateBoundingBoxEventArgs e)
    {
        if (IsTarget(e) && Preview.Bounds.IsValid) e.IncludeBoundingBox(Preview.Bounds);
    }
    protected override void CalculateBoundingBoxZoomExtents(CalculateBoundingBoxEventArgs e)
        => CalculateBoundingBox(e);

    // Shaded surfaces draw with the scene so depth and transparency behave like document objects.
    protected override void PostDrawObjects(DrawEventArgs e)
    {
        if (!IsTarget(e)) return;
        foreach (var item in Preview.Items)
            if (item.Material is { } material && !SourceSelected(e, item))
                switch (item.Geometry)
                {
                    case Mesh mesh:
                        if (item.UseVertexColors && material.Transparency == 0) e.Display.DrawMeshFalseColors(mesh);
                        // The two-argument overload suppresses vertex colors in RhinoCommon.
                        else if (item.VertexColorFaces is { } faces) e.Display.DrawMeshShaded(mesh, material, faces);
                        else e.Display.DrawMeshShaded(mesh, material);
                        break;
                    case Brep brep: e.Display.DrawBrepShaded(brep, material); break;
                    case SubD subd: e.Display.DrawSubDShaded(subd, material); break;
                }
    }

    // NOTE: McNeel documents DrawOverlay as feedback-only. Foreground also draws
    // the preview on ordinary redraws, so it remains visible outside commands.
    protected override void DrawForeground(DrawEventArgs e) => DrawPreview(e);
    protected override void DrawOverlay(DrawEventArgs e) => DrawPreview(e);

    private void DrawPreview(DrawEventArgs e)
    {
        if (!IsTarget(e)) return;
        foreach (var item in Preview.Items)
        {
            if (SourceSelected(e, item)) continue;
            if (item.Color.A == 0) continue;
            // A zero weight or size hides the wires or points, as zero shading hides the fill.
            if (item.Weight == 0 && item.Geometry is Curve or Mesh or Brep or SubD) continue;
            if (item.Size == 0 && item.Geometry is PointCloud or Point3d) continue;
            switch (item.Geometry)
            {
                case Curve curve: e.Display.DrawCurve(curve, item.Color, item.Weight); break;
                case Mesh mesh: e.Display.DrawMeshWires(mesh, item.Color, item.Weight); break;
                case Brep:
                    foreach (var edge in item.Edges ?? []) e.Display.DrawCurve(edge, item.Color, item.Weight);
                    break;
                case SubD subd: e.Display.DrawSubDWires(subd, item.Color, item.Weight); break;
                case PointCloud cloud: e.Display.DrawPointCloud(cloud, item.Size, item.Color); break;
                case Light light:
                    e.Display.DrawPoint(light.Location, PointStyle.RoundActivePoint, 5, item.Color);
                    e.Display.DrawLight(light, item.Color);
                    break;
                case AnnotationBase annotation: e.Display.DrawAnnotation(annotation, item.Color); break;
                case TextDot textDot: e.Display.DrawDot(textDot, item.Color, System.Drawing.Color.Black, item.Color); break;
                case Point3d point: e.Display.DrawPoint(point, PointStyle.Simple, item.Size, item.Color); break;
                case Line line: e.Display.DrawArrow(line, item.Color); break;
            }
        }
    }

    // A selected picked object keeps Rhino's own highlight visible instead of being covered by its input Preview.
    private static bool SourceSelected(DrawEventArgs e, PreviewItem item) =>
        item.Source != Guid.Empty && !item.SubObject && e.RhinoDoc?.Objects.FindId(item.Source) is { } source && source.IsSelected(false) != 0;
}
