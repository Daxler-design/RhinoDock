using Rhino.Geometry;

namespace rhDock.Rhino;

/// <summary>
/// The drawable shape of one picked Sub-object (ADR 0006), read from its object's current geometry: a Point3d for a
/// vertex, a Curve for an edge, a Mesh or Brep for a face. SubD parts use the control net. Unknown or out-of-range
/// parts return null so a stale index draws nothing instead of failing.
/// </summary>
public static class SubObjectGeometry
{
    public static object? Resolve(GeometryBase? geometry, ComponentIndex part)
    {
        var i = part.Index;
        if (i < 0) return null;
        geometry = geometry switch { Extrusion extrusion => extrusion.ToBrep(), Surface surface => surface.ToBrep(), _ => geometry };
        return geometry switch
        {
            SubD subd => part.ComponentIndexType switch
            {
                ComponentIndexType.SubdVertex => subd.Vertices.Find(i)?.ControlNetPoint,
                ComponentIndexType.SubdEdge => subd.Edges.Find(i) is { } edge ? new LineCurve(edge.ControlNetLine) : null,
                ComponentIndexType.SubdFace => subd.Faces.Find(i) is { } face ? Polygon(FaceCorners(face)) : null,
                _ => null
            },
            Mesh mesh => part.ComponentIndexType switch
            {
                ComponentIndexType.MeshVertex => i < mesh.Vertices.Count ? mesh.Vertices.Point3dAt(i) : null,
                ComponentIndexType.MeshTopologyVertex => i < mesh.TopologyVertices.Count ? (Point3d)mesh.TopologyVertices[i] : null,
                ComponentIndexType.MeshTopologyEdge => i < mesh.TopologyEdges.Count ? new LineCurve(mesh.TopologyEdges.EdgeLine(i)) : null,
                ComponentIndexType.MeshFace => i < mesh.Faces.Count ? Polygon(mesh.Faces[i].IsQuad
                    ? [mesh.Vertices.Point3dAt(mesh.Faces[i].A), mesh.Vertices.Point3dAt(mesh.Faces[i].B), mesh.Vertices.Point3dAt(mesh.Faces[i].C), mesh.Vertices.Point3dAt(mesh.Faces[i].D)]
                    : [mesh.Vertices.Point3dAt(mesh.Faces[i].A), mesh.Vertices.Point3dAt(mesh.Faces[i].B), mesh.Vertices.Point3dAt(mesh.Faces[i].C)]) : null,
                _ => null
            },
            Brep brep => part.ComponentIndexType switch
            {
                ComponentIndexType.BrepVertex => i < brep.Vertices.Count ? brep.Vertices[i].Location : null,
                ComponentIndexType.BrepEdge => i < brep.Edges.Count ? brep.Edges[i].DuplicateCurve() : null,
                ComponentIndexType.BrepFace => i < brep.Faces.Count ? brep.Faces[i].DuplicateFace(false) : null,
                _ => null
            },
            _ => null
        };
    }

    private static Point3d[] FaceCorners(SubDFace face)
    {
        var corners = new Point3d[face.EdgeCount];
        for (var k = 0; k < corners.Length; k++) corners[k] = face.VertexAt(k).ControlNetPoint;
        return corners;
    }

    /// <summary>A triangle or quad as a one-face mesh (shaded like a surface); larger polygons as a closed outline.</summary>
    private static GeometryBase? Polygon(Point3d[] corners)
    {
        if (corners.Length is 3 or 4)
        {
            var mesh = new Mesh();
            mesh.Vertices.AddVertices(corners);
            if (corners.Length == 3) mesh.Faces.AddFace(0, 1, 2); else mesh.Faces.AddFace(0, 1, 2, 3);
            mesh.Normals.ComputeNormals();
            return mesh;
        }
        return corners.Length > 3 ? new PolylineCurve([.. corners, corners[0]]) : null;
    }
}
