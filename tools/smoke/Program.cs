using rhDock.Core;
using rhDock.Agents;

if (args is ["--windows-handles"])
{
    await WindowsHandleChecks.Run();
    return 0;
}

if (args is ["--git-worktree", var gitRoot, var gitBranch])
{
    var tree = GitWorktreeInfo.Read(gitRoot) ?? throw new Exception("Git worktree not found.");
    if (!string.Equals(Path.GetFullPath(tree.Root), Path.GetFullPath(gitRoot), StringComparison.OrdinalIgnoreCase) || tree.Branch != gitBranch)
        throw new Exception("Git worktree identity mismatch: " + tree);
    Console.WriteLine("PASS: Git worktree root and branch match the checkout.");
    return 0;
}

if (args is ["--enum-input"])
{
    SingleScriptSessionChecks.CheckEnum();
    return 0;
}

if (args is ["--script-input-history"])
{
    ScriptInputUndoChecks.Run();
    return 0;
}

if (args is ["--extension-editors"])
{
    ExtensionScriptsChecks.Run();
    return 0;
}

if (args is ["--layout-writer", var layoutRepository])
{
    ScriptLayoutChecks.Writer(layoutRepository);
    return 0;
}

if (args is ["--layout"])
{
    ScriptLayoutChecks.Run();
    return 0;
}

// Disposable transport peer for offline question-response fixtures; it never invokes an agent or reads a repository.
if (args is ["--echo-agent"])
{
    while (Console.ReadLine() is { } line) Console.WriteLine(line);
    return 0;
}

if (args is ["--ui-bridge"])
{
    await SnapshotSenderChecks.Run();
    return 0;
}

if (args is ["--plugin-metadata", var plugin])
{
    PluginMetadataChecks.Run(plugin);
    return 0;
}

if (args is ["--host-tools", var cli, ..])
{
    await HostAccessChecks.Run(cli);
    if (args.Length == 3) HostCatalogChecks.Run(args[2]);
    return 0;
}

if (args is ["--agent-host"])
{
    await AgentHostChecks.Run();
    return 0;
}

if (args is ["--agent-host", var live, ..])
{
    string? Arg(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    await AgentHostChecks.RunLive(live, Arg("--model"), Arg("--effort"));
    return 0;
}

if (args is ["--agents-offline"])
{
    OfflineChecks.Run();
    AgentProtocolChecks.Run();
    return 0;
}

if (args is ["--agent", ..])
{
    string? Option(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    OfflineChecks.Run();
    await AgentChecks.RunLive(args.Length > 1 && !args[1].StartsWith("--") ? args[1] : Harnesses.DefaultId, Option("--path"), Option("--model"), args.Contains("--keep"));
    return 0;
}

if (args is ["--core-only"])
{
    ScriptProjectChecks.Run();
    SingleScriptSessionChecks.Run();
    AutoRunPacerChecks.Run();
    ScriptInputUndoChecks.Run();
    InputItemsChecks.Run();
    ExtensionScriptsChecks.Run();
    ScriptLayoutChecks.Run();
    ExtensionSlotChecks.Run();
    Console.WriteLine("PASS: direct Script and Workspace checks.");
    return 0;
}

if (args is not [var repository, var rhinoCommon] || !Directory.Exists(repository) || !File.Exists(rhinoCommon))
{
    Console.Error.WriteLine("Usage: smoke --core-only, smoke --agent-host, smoke --ui-bridge, smoke --plugin-metadata <plugin.rhp>, smoke --agents-offline, smoke --agent [<id>] [--path <executable>] [--model <model>] [--keep], or smoke <loaded-repository> <RhinoCommon.dll>");
    return 2;
}

using var compiler = new ScriptCompilerService(typeof(IPortBag).Assembly.Location, Path.GetFullPath(rhinoCommon));
using var session = new SingleScriptSession(compiler);
session.Open(repository);
if (session.Scripts.Count == 0) throw new Exception("No public Run(...) Scripts discovered in the Loaded Repository.");
Console.WriteLine($"PASS: compiled Loaded Repository and discovered {session.Scripts.Count} Scripts"
    + $" ({string.Join(", ", session.Scripts.Where(script => script.IsSolver).Select(script => script.Name).DefaultIfEmpty("no Solver Scripts"))}).");
var created = Path.Combine(Path.GetTempPath(), "rhdock-new-integration-" + Guid.NewGuid().ToString("N"));
try
{
    WorkingRepositoryTemplate.Create(created);
    using var starter = new SingleScriptSession(compiler);
    starter.Open(created);
    if (starter.Scripts.Count != 3 || starter.Scripts.Count(script => script.IsSolver) != 1)
        throw new Exception("New repository did not discover its three starter Scripts, including one Solver Script.");
    Console.WriteLine("PASS: New repository compiles and discovers its three starter Scripts, including one Solver Script.");
}
finally { if (Directory.Exists(created)) Directory.Delete(created, true); }
return 0;
