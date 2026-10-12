using System.Reflection;
using System.Runtime.Loader;
using System.Xml.Linq;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;

namespace rhDock.Core;

internal sealed record ProjectReference(string Path, MetadataReference Reference);

public sealed class ScriptCompilerService : IDisposable
{
    private readonly Dictionary<string, CompiledProject> cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly string[] extraReferences;
    // RHDOCK_DEBUG=1: unoptimized code with an embedded portable PDB and embedded sources, so an attached debugger can
    // stop in Script and Extension files. Read per compiler so a test can switch it.
    private readonly bool debug = Environment.GetEnvironmentVariable("RHDOCK_DEBUG") == "1";
    internal object Gate { get; } = new();
    private bool disposed;
    public IReadOnlyList<string> Messages { get; private set; } = Array.Empty<string>();
    public IReadOnlyList<string> LastReferences { get; private set; } = Array.Empty<string>();

    public ScriptCompilerService(params string[] extraReferences) => this.extraReferences = extraReferences.ToArray();

    public CompiledProject CompileProject(string projectFolder)
    {
        lock (Gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            projectFolder = Path.GetFullPath(projectFolder);
            var projectFile = FindProjectFile(projectFolder);
            var stamp = GetProjectStamp(projectFolder, projectFile);
            if (cache.TryGetValue(projectFolder, out var previous) && previous.LastWriteUtc == stamp) return previous;
            if (cache.Remove(projectFolder, out previous)) previous.Dispose();
            var project = Compile(projectFolder, projectFile, stamp);
            cache[projectFolder] = project;
            return project;
        }
    }

    /// <summary>
    /// Compiles a new generation without touching the cache, so the caller can keep its current generation until the
    /// new one is ready. The caller owns and disposes the result.
    /// </summary>
    public CompiledProject CompileUncached(string projectFolder)
    {
        lock (Gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            projectFolder = Path.GetFullPath(projectFolder);
            var projectFile = FindProjectFile(projectFolder);
            return Compile(projectFolder, projectFile, GetProjectStamp(projectFolder, projectFile));
        }
    }

    private static string FindProjectFile(string projectFolder) => Directory.GetFiles(projectFolder, "*.csproj").FirstOrDefault()
        ?? throw new FileNotFoundException("No .csproj found in project folder.", projectFolder);

    private CompiledProject Compile(string projectFolder, string projectFile, DateTime stamp)
    {
        var files = Directory.GetFiles(projectFolder, "*.cs", SearchOption.AllDirectories).Where(p => !IsBuildOutput(p)).ToArray();
        if (files.Length == 0) throw new InvalidOperationException("Project contains no .cs files.");
        var options = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);
        // Read bytes, not strings, so the debug PDB checksum matches the file a debugger opens.
        var trees = files.Select(p =>
        {
            using var file = File.OpenRead(p);
            return CSharpSyntaxTree.ParseText(SourceText.From(file, canBeEmbedded: debug), options, p);
        }).ToArray();
        var messages = new List<string>();
        var paths = BuildBaseReferences();
        var dependencies = ReadProjectReferences(projectFile, messages, paths).ToArray();
        foreach (var reference in dependencies) paths.Add(reference.Path);
        LastReferences = paths.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (!paths.Any(p => Path.GetFileName(p).Equals("Grasshopper.dll", StringComparison.OrdinalIgnoreCase)))
            messages.Add("Grasshopper.dll not found in host/install references; GH-dependent scripts may not compile.");
        Messages = messages.ToArray();
        var compilation = CSharpCompilation.Create($"rhDock.Script.{Guid.NewGuid():N}", trees,
            paths.Select(p => MetadataReference.CreateFromFile(p)),
            new CSharpCompilationOptions(Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: debug ? OptimizationLevel.Debug : OptimizationLevel.Release));
        using var stream = new MemoryStream();
        var result = debug
            ? compilation.Emit(stream, embeddedTexts: trees.Select(tree => EmbeddedText.FromSource(tree.FilePath, tree.GetText())),
                options: new EmitOptions(debugInformationFormat: DebugInformationFormat.Embedded))
            : compilation.Emit(stream);
        if (!result.Success) throw new ScriptCompileException(string.Join(Environment.NewLine,
            result.Diagnostics.Where(d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)));
        stream.Position = 0;
        var context = new ScriptAssemblyLoadContext(paths);
        try
        {
            var assembly = context.LoadFromStream(stream);
            var candidates = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var tree in trees)
            {
                var names = tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>()
                    .Where(c => c.Members.OfType<MethodDeclarationSyntax>().Any(m => m.Identifier.ValueText == "Run"))
                    .Select(GetFullTypeName).ToArray();
                candidates[Path.GetRelativePath(projectFolder, tree.FilePath).Replace('\\', '/')] = names;
                candidates[Path.GetFileName(tree.FilePath)] = names;
            }
            return new CompiledProject(projectFolder, assembly, context, stamp, candidates);
        }
        catch { context.Unload(); throw; }
    }

    public void Invalidate(string projectFolder)
    {
        lock (Gate)
            if (cache.Remove(Path.GetFullPath(projectFolder), out var project)) project.Dispose();
    }

    private static DateTime GetProjectStamp(string folder, string projectFile)
    {
        var stamp = File.GetLastWriteTimeUtc(projectFile);
        foreach (var file in Directory.GetFiles(folder, "*.cs", SearchOption.AllDirectories))
            if (!IsBuildOutput(file) && File.GetLastWriteTimeUtc(file) > stamp) stamp = File.GetLastWriteTimeUtc(file);
        return stamp;
    }

    internal static bool IsBuildOutput(string path) => path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
        .Any(p => p.Equals("bin", StringComparison.OrdinalIgnoreCase) || p.Equals("obj", StringComparison.OrdinalIgnoreCase));

    private HashSet<string> BuildBaseReferences()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? "").Split(Path.PathSeparator)) AddPath(paths, p);
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
        {
            // Never reference another collectible generation or query Location on a dynamic assembly.
            if (a.IsDynamic || AssemblyLoadContext.GetLoadContext(a)?.IsCollectible == true) continue;
            AddPath(paths, a.Location);
            if (a.GetName().Name == "RhinoCommon")
            {
                var system = Path.GetDirectoryName(a.Location)!;
                // Rhino's .NET host can load System/netcore/RhinoCommon.dll.
                if (Path.GetFileName(system).Equals("netcore", StringComparison.OrdinalIgnoreCase))
                    system = Path.GetDirectoryName(system)!;
                AddPath(paths, Path.GetFullPath(Path.Combine(system, "..", "Plug-ins", "Grasshopper", "Grasshopper.dll")));
                AddPath(paths, Path.GetFullPath(Path.Combine(system, "..", "Plug-ins", "Grasshopper", "GH_IO.dll")));
            }
        }
        foreach (var p in extraReferences) AddPath(paths, p);
        return paths;
    }

    private static void AddPath(HashSet<string> paths, string path)
    {
        if (File.Exists(path) && !Path.GetFileNameWithoutExtension(path).Equals("Rhino3dm", StringComparison.OrdinalIgnoreCase)) paths.Add(Path.GetFullPath(path));
    }

    private static IEnumerable<ProjectReference> ReadProjectReferences(string projectFile, List<string> messages, HashSet<string> hostReferences)
    {
        foreach (var reference in XDocument.Load(projectFile).Descendants().Where(e => e.Name.LocalName == "Reference"))
        {
            var name = reference.Attribute("Include")?.Value.Split(',')[0];
            var hint = reference.Elements().FirstOrDefault(e => e.Name.LocalName == "HintPath")?.Value;
            if (string.IsNullOrWhiteSpace(hint)) continue;
            // HintPath uses MSBuild $(Name) syntax, not just Windows %Name% syntax.
            // Resolve environment properties without pretending to evaluate a full MSBuild project.
            var expanded = Regex.Replace(hint.Trim(), @"\$\(([A-Za-z_][A-Za-z0-9_]*)\)",
                match => Environment.GetEnvironmentVariable(match.Groups[1].Value) ?? match.Value);
            if (expanded.Contains("$(", StringComparison.Ordinal))
            {
                if (!hostReferences.Any(path => Path.GetFileNameWithoutExtension(path).Equals(name, StringComparison.OrdinalIgnoreCase)))
                    messages.Add($"Unresolved reference HintPath: {hint}");
                continue;
            }
            var path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(expanded), Path.GetDirectoryName(projectFile)!);
            if (File.Exists(path)) yield return new(path, MetadataReference.CreateFromFile(path));
            else messages.Add($"Reference HintPath not found: {path}");
        }
    }

    private static string GetFullTypeName(ClassDeclarationSyntax type)
    {
        var classes = type.AncestorsAndSelf().OfType<ClassDeclarationSyntax>().Reverse().Select(c => c.Identifier.ValueText);
        var ns = string.Join(".", type.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(n => n.Name.ToString()));
        return (ns.Length == 0 ? "" : ns + ".") + string.Join("+", classes);
    }

    public void Dispose()
    {
        lock (Gate)
        {
            if (disposed) return;
            disposed = true;
            foreach (var project in cache.Values) project.Dispose();
            cache.Clear();
        }
    }
}
