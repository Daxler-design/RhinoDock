namespace rhDock.Core;

/// <summary>Creates a small Loaded Repository from the templates packaged with rhDock.</summary>
public static class WorkingRepositoryTemplate
{
    private static readonly string[] Files =
    [
        ".gitignore", "AGENTS.md", "CLAUDE.md", "MyProject.csproj", "lib/.gitkeep",
        "Core/MyCore.cs", "Core/Falloff.cs", "Core/HexGrid.cs", "Core/PointSmoothing.cs",
        "Domain/MyDomain.cs",
        "Domain/CurveSmoothing/CONTEXT.md", "Domain/CurveSmoothing/CurveSmoothingDomain.cs",
        "Domain/HexAttractor/CONTEXT.md", "Domain/HexAttractor/HexAttractorDomain.cs",
        "Components/MyComponent.cs", "Components/CurveSmoothingComponent.cs", "Components/HexAttractorComponent.cs"
    ];

    public static void Create(string destination)
    {
        if (string.IsNullOrWhiteSpace(destination)) throw new ArgumentException("Choose an empty folder for the new repository.", nameof(destination));
        var root = Path.GetFullPath(destination);
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new IOException("New requires an empty folder. Use Browse to open an existing repository.");
        Directory.CreateDirectory(root);
        var assembly = typeof(WorkingRepositoryTemplate).Assembly;
        foreach (var file in Files)
        {
            var resource = "rhDock.Core.Template." + file.Replace('/', '.');
            using var source = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException("Missing packaged repository template: " + file);
            var target = Path.Combine(root, file);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
            source.CopyTo(output);
        }
    }
}
