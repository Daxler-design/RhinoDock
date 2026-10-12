using Microsoft.CodeAnalysis.CSharp;

namespace rhDock.Core;

/// <summary>Creates an Extension Repo, and Extensions inside it, from the templates packaged with rhDock.</summary>
public static class ExtensionRepositoryTemplate
{
    public const string StarterName = "MyExtension";
    private static readonly string[] RepositoryFiles = [".gitignore", "AGENTS.md", "CLAUDE.md"];
    private static readonly string[] ExtensionFiles = ["MyExtension.csproj", "MyExtension.cs", "index.html", "rhdock.js"];

    /// <summary>Creates the repo files and the starter Extension in an empty or new folder.</summary>
    public static void CreateRepository(string destination)
    {
        if (string.IsNullOrWhiteSpace(destination)) throw new ArgumentException("Choose an empty folder for the new Extension Repo.", nameof(destination));
        var root = Path.GetFullPath(destination);
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new IOException("A new Extension Repo needs an empty folder. Use Open to use an existing one.");
        Directory.CreateDirectory(root);
        foreach (var file in RepositoryFiles) Copy(file, Path.Combine(root, file), null);
        CreateExtension(root, StarterName);
    }

    /// <summary>Creates <c>repository/name</c> from the starter Extension, renaming its class, project, and window title.</summary>
    public static void CreateExtension(string repository, string name)
    {
        if (!SyntaxFacts.IsValidIdentifier(name) || SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None)
            throw new ArgumentException("An Extension name must be a C# identifier, such as SunStudy.", nameof(name));
        var folder = Path.Combine(Path.GetFullPath(repository), name);
        if (Directory.Exists(folder)) throw new IOException($"The Extension Repo already has a folder named {name}.");
        Directory.CreateDirectory(folder);
        foreach (var file in ExtensionFiles) Copy(StarterName + "/" + file, Path.Combine(folder, file.Replace(StarterName, name)), name);
    }

    private static void Copy(string file, string target, string? name)
    {
        var resource = "rhDock.Core.ExtensionTemplate." + file.Replace('/', '.');
        using var source = typeof(ExtensionRepositoryTemplate).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException("Missing packaged Extension template: " + file);
        using var reader = new StreamReader(source);
        var text = reader.ReadToEnd();
        using var output = new StreamWriter(new FileStream(target, FileMode.CreateNew, FileAccess.Write));
        output.Write(name is null ? text : text.Replace(StarterName, name));
    }
}
