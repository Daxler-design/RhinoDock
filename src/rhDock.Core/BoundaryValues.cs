using System.Collections;
using System.Runtime.Loader;

namespace rhDock.Core;

/// <summary>
/// Values that cross between Scripts and Extensions. Only types from non-collectible assemblies (.NET, RhinoCommon) may
/// cross, and mutable ones are copied, so neither side holds the other's types or objects after a reload.
/// </summary>
public static class BoundaryValues
{
    /// <summary>
    /// Copies <paramref name="value"/> for the other side. Strings and values pass through; collections become arrays of
    /// copied items; other objects go through <paramref name="duplicate"/> (for example RhinoCommon geometry), which
    /// returns null for types it cannot copy. Returns false when any part of the value cannot cross.
    /// </summary>
    public static bool TryCopy(object? value, Func<object, object?> duplicate, out object? copy)
    {
        copy = null;
        if (value is null) return true;
        var type = value.GetType();
        if (IsCollectible(type)) return false;
        if (value is string || type.IsValueType) { copy = value; return true; }
        if (duplicate(value) is { } duplicated) { copy = duplicated; return !IsCollectible(duplicated.GetType()); }
        if (value is not IEnumerable items) return false;
        var copies = new List<object?>();
        foreach (var item in items)
        {
            if (!TryCopy(item, duplicate, out var itemCopy)) return false;
            copies.Add(itemCopy);
        }
        // The nearest common base type, so NurbsCurve and LineCurve items become a Curve[].
        var element = copies.Count == 0 || copies.Contains(null) ? typeof(object) : copies[0]!.GetType();
        while (copies.Any(item => !element.IsInstanceOfType(item))) element = element.BaseType ?? typeof(object);
        var array = Array.CreateInstance(element, copies.Count);
        for (int i = 0; i < copies.Count; i++) array.SetValue(copies[i], i);
        copy = array;
        return true;
    }

    /// <summary>True for types from .NET or RhinoCommon; false for types a Script or Extension defined.</summary>
    public static bool CanCross(Type type) => !IsCollectible(type);

    private static bool IsCollectible(Type type) =>
        AssemblyLoadContext.GetLoadContext(type.Assembly)?.IsCollectible == true
        || type.IsGenericType && type.GetGenericArguments().Any(IsCollectible)
        || type.HasElementType && IsCollectible(type.GetElementType()!);
}
