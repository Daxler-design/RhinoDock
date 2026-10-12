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

    // Rhino may load plug-ins outside the default context. Extensions must bind to the host's own rhDock assemblies,
    // or their contract types would be second copies that the host cannot recognize.
    private static readonly AssemblyLoadContext HostContext =
        GetLoadContext(typeof(ScriptAssemblyLoadContext).Assembly) ?? Default;

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var shared = Default.Assemblies.Concat(HostContext == Default ? [] : HostContext.Assemblies)
            .FirstOrDefault(assembly => AssemblyName.ReferenceMatchesDefinition(assembly.GetName(), assemblyName));
        if (shared is not null) return shared;

        return assemblyName.Name is { } name && dependencyPaths.TryGetValue(name, out var path)
            ? LoadFromAssemblyPath(path)
            : null;
    }
}
