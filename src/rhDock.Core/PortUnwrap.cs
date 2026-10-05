using System.Reflection;

namespace rhDock.Core;

public static class PortUnwrap
{
    public static object? Unwrap(object? value)
    {
        if (value is null) return null;
        try
        {
            var goo = value.GetType().GetInterfaces().FirstOrDefault(i => i.Name == "IGH_Goo");
            var method = goo?.GetMethod("ScriptVariable", Type.EmptyTypes);
            return method is null ? value : method.Invoke(value, null);
        }
        catch (Exception e) when (e is TypeLoadException or FileNotFoundException or FileLoadException or ReflectionTypeLoadException)
        { return value; }
    }
}
