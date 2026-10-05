using System.Reflection;
using System.Runtime.InteropServices;

static class PluginMetadataChecks
{
    public static void Run(string path)
    {
        var assembly = Assembly.LoadFrom(Path.GetFullPath(path));
        var value = assembly.GetCustomAttributesData()
            .SingleOrDefault(attribute => attribute.AttributeType == typeof(GuidAttribute))?
            .ConstructorArguments[0].Value as string;
        if (!Guid.TryParse(value, out var id) || id != Guid.Parse("F48C10C1-44B2-47BD-B817-D6A8FE399B76"))
            throw new Exception("The Rhino plug-in assembly must declare the stable rhDock GUID for panel registration.");
        Console.WriteLine($"PASS: Rhino plug-in assembly GUID {id}.");
    }
}
