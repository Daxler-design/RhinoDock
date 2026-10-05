using System.Reflection;
using System.Runtime.Loader;

namespace rhDock.Core;

internal sealed class ScriptAssemblyLoadContext : AssemblyLoadContext
{
    private readonly Dictionary<string, string> dependencyPaths;

    public ScriptAssemblyLoadContext(IEnumerable<string> dependencyPaths)
        : base(isCollectible: true)
    {
        this.dependencyPaths = dependencyPaths
            .Where(File.Exists)
            .GroupBy(
                path => AssemblyName.GetAssemblyName(path).Name ?? Path.GetFileNameWithoutExtension(path),
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var shared = AssemblyLoadContext.Default.Assemblies
            .FirstOrDefault(assembly => AssemblyName.ReferenceMatchesDefinition(assembly.GetName(), assemblyName));
        if (shared is not null) return shared;

        return assemblyName.Name is { } name && dependencyPaths.TryGetValue(name, out var path)
            ? LoadFromAssemblyPath(path)
            : null;
    }
}
