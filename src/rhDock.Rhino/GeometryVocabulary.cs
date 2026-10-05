using Rhino.DocObjects;
using Rhino.Geometry;

namespace rhDock.Rhino;

/// <summary>
/// Contract §2.1 rules 1–4 for pickers. The vocabulary is every public GeometryBase subclass reflected from
/// RhinoCommon (RH9 additions pick automatically). Only the behavior table below is hand-kept, and its values are
/// ObjectType combinations only; unlisted subclasses pick with AnyObject and a runtime type check.
/// RhinoCommon is never collectible, so caching its Type objects is outside the script ALC discipline.
/// </summary>
public static class GeometryVocabulary
{
    private static readonly Dictionary<string, Type> Types = typeof(GeometryBase).Assembly.GetExportedTypes()
        .Where(t => t == typeof(GeometryBase) || t.IsSubclassOf(typeof(GeometryBase))).ToDictionary(t => t.FullName!, StringComparer.Ordinal);

    // Most specific first: ClippingPlaneSurface/Extrusion derive from Surface.
    private static readonly (Type Type, ObjectType Filter)[] Behavior =
    [
        (typeof(Curve), ObjectType.Curve),
        (typeof(Brep), ObjectType.Brep | ObjectType.Surface | ObjectType.Extrusion),
        (typeof(ClippingPlaneSurface), ObjectType.ClipPlane),
        (typeof(Extrusion), ObjectType.Extrusion),
        (typeof(Surface), ObjectType.Surface | ObjectType.Extrusion),
        (typeof(Mesh), ObjectType.Mesh),
        (typeof(SubD), ObjectType.SubD),
        (typeof(global::Rhino.Geometry.Point), ObjectType.Point),
        (typeof(PointCloud), ObjectType.PointSet),
        (typeof(Light), ObjectType.Light),
        (typeof(TextDot), ObjectType.TextDot),
        (typeof(AnnotationBase), ObjectType.Annotation),
        (typeof(Hatch), ObjectType.Hatch),
        (typeof(InstanceReferenceGeometry), ObjectType.InstanceReference),
        (typeof(DetailView), ObjectType.Detail),
        (typeof(MorphControl), ObjectType.MorphControl),
    ];

    /// <summary>The GeometryBase type a port element names, or null when it is not geometry (object ports resolve to GeometryBase).</summary>
    public static Type? Resolve(string elementTypeName) =>
        elementTypeName is "System.Object" ? typeof(GeometryBase) : Types.GetValueOrDefault(elementTypeName);

    public static ObjectType FilterFor(Type type) =>
        Behavior.FirstOrDefault(b => b.Type.IsAssignableFrom(type)).Filter is var filter && filter != ObjectType.None ? filter
        : type == typeof(GeometryBase) ? ObjectType.AnyObject & ~(ObjectType.Grip | ObjectType.Phantom | ObjectType.Cage | ObjectType.InstanceDefinition)
        : ObjectType.AnyObject;

    /// <summary>Point3d ports pick Rhino point objects and receive their locations.</summary>
    public static bool IsPoint3d(string elementTypeName) => elementTypeName == typeof(Point3d).FullName;

    /// <summary>ComponentIndex ports pick Sub-objects (ADR 0006): a part of a SubD, Mesh, or Brep, received as a Rhino ComponentIndex.</summary>
    public static bool IsComponentIndex(string elementTypeName) => elementTypeName == typeof(ComponentIndex).FullName;

    public static bool CanPick(string elementTypeName) => IsPoint3d(elementTypeName) || IsComponentIndex(elementTypeName) || Resolve(elementTypeName) is not null;

    public static ObjectType PickFilter(string elementTypeName) =>
        IsPoint3d(elementTypeName) ? ObjectType.Point
        : IsComponentIndex(elementTypeName) ? ObjectType.SubD | ObjectType.Mesh | ObjectType.Brep | ObjectType.Surface | ObjectType.Extrusion
            | ObjectType.MeshVertex | ObjectType.MeshEdge | ObjectType.MeshFace | ObjectType.BrepVertex | ObjectType.EdgeFilter
        : FilterFor(Resolve(elementTypeName)!);

    /// <summary>The host-owned value for one picked object: a Point3d location or a detached geometry copy.</summary>
    public static object? ExtractPick(ObjRef reference, string elementTypeName) =>
        IsPoint3d(elementTypeName) ? reference.Point()?.Location
        : Extract(reference, Resolve(elementTypeName)!);

    public static object PickValue(IReadOnlyList<object> values, string elementTypeName, bool list) =>
        !list ? values[0]
        : IsPoint3d(elementTypeName) ? values.Cast<Point3d>().ToArray()
        : IsComponentIndex(elementTypeName) ? values.Cast<ComponentIndex>().ToArray()
        : values.Cast<GeometryBase>().ToArray();

    /// <summary>Preview Style family: which size controls apply to a geometry value.</summary>
    public static string? Family(object leaf) => leaf switch
    {
        Curve or Line or Polyline => "curve",
        Point3d or global::Rhino.Geometry.Point or PointCloud => "point",
        Brep or Surface or Mesh or SubD => "surface",
        GeometryBase => "other",
        _ => leaf.GetType().GetMethod("toRhinoMesh", Type.EmptyTypes)?.ReturnType == typeof(Mesh) ? "surface" : null
    };

    public static string? FamilyOf(string elementTypeName)
    {
        if (IsPoint3d(elementTypeName)) return "point";
        if (IsComponentIndex(elementTypeName)) return "mixed";
        var type = Resolve(elementTypeName);
        return type is null ? null : type == typeof(GeometryBase) ? "mixed"
            : typeof(Curve).IsAssignableFrom(type) ? "curve"
            : type == typeof(global::Rhino.Geometry.Point) || type == typeof(PointCloud) ? "point"
            : typeof(Brep).IsAssignableFrom(type) || typeof(Surface).IsAssignableFrom(type) || type == typeof(Mesh) || type == typeof(SubD) ? "surface"
            : "other";
    }

    /// <summary>A short noun for picked counts, such as "2 curves".</summary>
    public static string CountLabel(string elementTypeName, int count)
    {
        var noun = IsPoint3d(elementTypeName) ? "point" : IsComponentIndex(elementTypeName) ? "sub-object" : Resolve(elementTypeName) switch
        {
            null => "object",
            var t when t == typeof(GeometryBase) => "object",
            var t when typeof(Curve).IsAssignableFrom(t) => "curve",
            var t when t == typeof(global::Rhino.Geometry.Point) => "point",
            var t when t == typeof(Mesh) => "mesh",
            var t => t.Name
        };
        return count + " " + (count == 1 ? noun : noun == "mesh" ? "meshes" : noun + "s");
    }

    /// <summary>A bounded, human-readable description of one value for element tables.</summary>
    public static string Describe(object value) => value switch
    {
        ComponentIndex part => DescribeComponent(part),
        Point3d point => FormatPoint(point),
        global::Rhino.Geometry.Point point => FormatPoint(point.Location),
        Polyline polyline => polyline.Count == 0 ? "Empty polyline" : $"Polyline · {polyline.Count} points · start {FormatPoint(polyline[0])}",
        PolylineCurve polyline => $"PolylineCurve · {polyline.PointCount} points · {(polyline.IsClosed ? "closed" : "open")}",
        NurbsCurve nurbs => $"NurbsCurve · degree {nurbs.Degree} · {nurbs.Points.Count} control points · {(nurbs.IsClosed ? "closed" : "open")}",
        Curve curve => $"{curve.GetType().Name} · {(curve.IsClosed ? "closed" : "open")}",
        Brep brep => $"Brep · {brep.Faces.Count} face{(brep.Faces.Count == 1 ? "" : "s")} · {(brep.IsSolid ? "closed" : "open")}",
        Mesh mesh => $"Mesh · {mesh.Vertices.Count} vertices · {mesh.Faces.Count} faces",
        SubD subd => $"SubD · {subd.Faces.Count} faces",
        PointCloud cloud => $"PointCloud · {cloud.Count} points",
        TextDot dot => $"{dot.Text[..Math.Min(dot.Text.Length, 80)]} · {FormatPoint(dot.Point)}",
        GeometryBase geometry => geometry.GetType().Name,
        _ => value.GetType().Name
    };

    /// <summary>The Rhino component type name and index, such as "SubdEdge 12".</summary>
    public static string DescribeComponent(ComponentIndex part) => $"{part.ComponentIndexType} {part.Index}";

    public static string FormatPoint(Point3d point) => string.Format(System.Globalization.CultureInfo.InvariantCulture,
        "({0:0.###}, {1:0.###}, {2:0.###})", point.X, point.Y, point.Z);

    /// <summary>A detached GeometryBase copy (rule 4: never the RhinoObject/LightObject shell), or null if the object is not that type.</summary>
    public static GeometryBase? Extract(ObjRef reference, Type type)
    {
        var geometry = type == typeof(Brep) ? reference.Brep()
            : type == typeof(Surface) ? reference.Surface()
            : typeof(Curve).IsAssignableFrom(type) ? reference.Curve()
            : type == typeof(Mesh) ? reference.Mesh()
            : reference.Geometry();
        var copy = geometry?.Duplicate();
        if (copy is null || type.IsInstanceOfType(copy)) return copy;
        copy.Dispose();
        return null;
    }
}
