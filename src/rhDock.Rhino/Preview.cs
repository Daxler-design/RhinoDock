using System.Drawing;
using Rhino.Display;
using Rhino.Geometry;

namespace rhDock.Rhino;

/// <summary>One drawable value with its resolved Preview Style. Edges and material are prepared once, not per redraw.</summary>
public sealed class PreviewItem : IDisposable
{
    public PreviewItem(object geometry, Color color, int weight, int size, int shading, Guid source = default, bool colorOverridden = true, bool subObject = false, Color? fillColor = null)
    {
        SubObject = subObject;
        UseVertexColors = !colorOverridden && geometry is Mesh { VertexColors.Count: > 0 } mesh && mesh.VertexColors.Count == mesh.Vertices.Count;
        if (UseVertexColors && geometry is Mesh colored)
            VertexColorFaces = Enumerable.Range(0, colored.Faces.Count).ToArray();
        Source = source;
        Geometry = geometry;
        Color = color;
        Weight = weight;
        Size = size;
        if (shading > 0 && geometry is Brep or Mesh or SubD)
            Material = new DisplayMaterial(UseVertexColors ? Color.White : fillColor ?? color, 1.0 - shading / 100.0);
        // This is the host-owned preview copy; never clear colors on the Script's output.
        if (colorOverridden && geometry is Mesh overridden) overridden.VertexColors.Clear();
        if (geometry is Brep brep) Edges = brep.DuplicateEdgeCurves(false);
    }

    public object Geometry { get; }
    /// <summary>The picked Rhino object this item previews, or empty for outputs.</summary>
    public Guid Source { get; }
    /// <summary>A picked part of the source object; it stays drawn while its object is selected, since Rhino's own highlight is subtle.</summary>
    public bool SubObject { get; }
    public Color Color { get; }
    public int Weight { get; }
    public int Size { get; }
    public DisplayMaterial? Material { get; }
    /// <summary>Shaded drawing uses the mesh's own vertex colors because the user set no color override.</summary>
    public bool UseVertexColors { get; }
    /// <summary>The indexed shaded-mesh overload preserves vertex colors; prepare its face list once per preview.</summary>
    internal int[]? VertexColorFaces { get; }
    public Curve[]? Edges { get; }

    public void Dispose()
    {
        (Geometry as GeometryBase)?.Dispose();
        Material?.Dispose();
        foreach (var edge in Edges ?? []) edge.Dispose();
    }
}

public sealed record Preview(IReadOnlyList<PreviewItem> Items) : IDisposable
{
    public static Preview Empty => new([]);
    public BoundingBox Bounds
    {
        get
        {
            var box = BoundingBox.Empty;
            foreach (var item in Items)
                switch (item.Geometry)
                {
                    case GeometryBase geometry: box.Union(geometry.GetBoundingBox(false)); break;
                    case Point3d point: box.Union(point); break;
                    case Line line: box.Union(line.From); box.Union(line.To); break;
                }
            return box;
        }
    }
    public void Dispose()
    {
        foreach (var item in Items) item.Dispose();
    }
}
