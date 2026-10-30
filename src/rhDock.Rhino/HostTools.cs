using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Rhino;
using Rhino.Commands;
using rhDock.Agents;
using rhDock.RhinoAI;
using rhDock.RhinoAI.Server;

namespace rhDock.Rhino;

internal static class HostTools
{
    private static readonly SemaphoreSlim execution = new(1, 1);
    private static readonly string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "rhDock", "host-tools");
    private static string? setupError;

    internal static void Initialize()
    {
        AgentHost.CreateHostConnection = Connect;
        try
        {
            var cli = Path.Combine(Path.GetDirectoryName(typeof(HostTools).Assembly.Location)!, "cli", "rhdock.dll");
            if (!File.Exists(cli)) throw new FileNotFoundException("The rhDock CLI payload is missing. Reinstall the complete package.");
            var runtime = FindRuntime();
            var bin = Path.Combine(directory, "bin", Environment.ProcessId.ToString());
            Directory.CreateDirectory(bin);
            var launcher = Path.Combine(bin, OperatingSystem.IsWindows() ? "rhdock.cmd" : "rhdock");
            if (OperatingSystem.IsWindows())
                File.WriteAllText(launcher, $"@echo off\r\n\"{runtime}\" \"{cli}\" %*\r\n");
            else
            {
                static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";
                File.WriteAllText(launcher, $"#!/bin/sh\nexec {Quote(runtime)} {Quote(cli)} \"$@\"\n");
                File.SetUnixFileMode(launcher, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            File.Copy(launcher, Path.Combine(directory, "bin", Path.GetFileName(launcher)), overwrite: true);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path.Combine(directory, "bin", Path.GetFileName(launcher)),
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            AgentProcess.HostToolsPath = bin;
        }
        catch (Exception ex) { setupError = ex.Message; HostLog.Write("host tools setup: " + ex.Message); }
    }

    private static string FindRuntime()
    {
        var executable = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        for (var folder = new DirectoryInfo(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory()); folder is not null; folder = folder.Parent)
            if (File.Exists(Path.Combine(folder.FullName, executable))) return Path.Combine(folder.FullName, executable);
        return AgentProcess.Find("dotnet", null, AgentProcess.ExtendedPath()) ?? throw new FileNotFoundException("A .NET runtime is required to start the rhDock CLI.");
    }

    // A target change invalidates queued work without revoking the instance grant.
    private static readonly string instanceId = Guid.NewGuid().ToString("N");
    private static long targetVersion;
    internal static void TargetChanged() => Interlocked.Increment(ref targetVersion);
    private static string Target() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Serialize(new
    {
        instance = instanceId, version = Interlocked.Read(ref targetVersion), document = RhinoDoc.ActiveDoc?.RuntimeSerialNumber,
        repository = SingleScriptHost.Current.Session.Repository,
        scriptDocument = SingleScriptHost.Current.BoundDocument?.RuntimeSerialNumber
    }))));

    private static HostConnection Connect()
    {
        if (setupError is not null) throw new InvalidOperationException(setupError);
        return new HostConnection(Path.Combine(directory, "connections"), async (request, ct) =>
        {
            if (request.Method == "status")
                return await OnUi(() => Serialize(new
                {
                    pid = Environment.ProcessId, target = Target(),
                    document = RhinoDoc.ActiveDoc?.RuntimeSerialNumber, path = RhinoDoc.ActiveDoc?.Path,
                    repository = SingleScriptHost.Current.Session.Repository
                }), ct);
            var (doc, host, atRevision) = await OnUi(() =>
            {
                if (request.Method is "call" or "resource") request.CheckTarget(Target());
                return (RhinoDoc.ActiveDoc ?? throw new InvalidOperationException("Open a Rhino document first."),
                    SingleScriptHost.Current, SingleScriptHost.Current.Revision);
            }, ct);
            void Check()
            {
                ct.ThrowIfCancellationRequested();
                request.CheckTarget(Target());
                if (request.Name?.StartsWith("rhdock_", StringComparison.Ordinal) == true && host.BoundDocument != doc)
                    throw new InvalidOperationException("Activate the rhDock Script document before using Script tools.");
            }
            var services = new DocumentServices(doc);
            var registry = ToolRegistry.Scan(typeof(HostTools).Assembly, services);
            var resources = ResourceRegistry.Scan(typeof(HostTools).Assembly, services);
            if (request.Method == "tools") return Serialize(new { tools = registry.All.Where(t => !t.InPanelOnly).Select(t => new { t.Name, t.Description, readOnly = t.ReadOnly }) });
            if (request.Method == "describe")
                return registry.TryGet(request.Name ?? "", out var described) && !described.InPanelOnly
                    ? Serialize(Descriptor(described)) : throw new ArgumentException("Unknown tool: " + request.Name);
            if (request.Method == "resources") return Serialize(new { resources = resources.All.Select(r => new { r.Name, r.UriTemplate, r.Description, r.MimeType }) });
            if (request.Method == "resource")
            {
                var resource = resources.Match(request.Name ?? "", out var variables) ?? throw new ArgumentException("Unknown resource URI.");
                return await OnUiAsync(async () => { Check(); return Serialize(await resource.InvokeAsync(request.Name!, variables, services, ct)); }, ct);
            }
            if (request.Method != "call") throw new ArgumentException("Unknown request method.");
            if (!registry.TryGet(request.Name ?? "", out var tool) || tool.InPanelOnly) throw new ArgumentException("Unknown tool: " + request.Name);
            if (request.Arguments.ValueKind != JsonValueKind.Object) throw new ArgumentException("Tool arguments must be a JSON object.");
            var detail = request.Arguments.GetRawText();
            await execution.WaitAsync(ct);
            try
            {
                AgentHost.RecordInstanceTool(request.Source, tool.Name, "InProgress", detail, request.Id!);
                var arguments = request.Arguments.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
                // Cocoa must process the document activation before a command such as Undo is dispatched.
                if (tool.Name is "run_command" or "run_python" or "run_csharp" or "close_doc")
                    await OnUi(() => { Check(); RhinoApp.SetFocusToMainWindow(doc); return true; }, ct);
                var result = await tool.InvokeAsync(arguments, services, ct, () =>
                {
                    Check();
                    if (host.Revision != atRevision) throw new InvalidOperationException("The Script changed while this request was waiting. Read fresh state and request again.");
                    if (host.Running || host.ActivePick is not null || Command.InCommand()) throw new InvalidOperationException("Rhino or rhDock is busy. Wait until the current operation finishes.");
                    if (!tool.ReadOnly) DocumentSaveDiagnostics.Write("tool-begin:" + tool.Name + ":" + request.Id, doc);
                });
                var json = Serialize(result);
                AgentHost.RecordInstanceTool(request.Source, tool.Name, result.IsError ? "Failed" : "Completed", detail, request.Id!, json);
                await OnUi(() =>
                {
                    if (!tool.ReadOnly) DocumentSaveDiagnostics.Write("tool-end:" + tool.Name + ":" + request.Id, doc);
                    host.Refresh(); return true;
                }, ct);
                return json;
            }
            catch (Exception ex) { AgentHost.RecordInstanceTool(request.Source, tool.Name, "Failed", ex.Message, request.Id!); throw; }
            finally { execution.Release(); }
        }, publishInstance: true);
    }

    private static object Descriptor(ToolHandler tool) => new { tool.Name, tool.Title, tool.Description, tool.InputSchema, readOnly = tool.ReadOnly, destructive = tool.Destructive };
    internal static string Serialize(object value) => JsonSerializer.Serialize(value, McpSerializer.Options);

    private static Task<T> OnUi<T>(Func<T> action, CancellationToken ct) => OnUiAsync(() => Task.FromResult(action()), ct);
    private static Task<T> OnUiAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = ct.Register(() => result.TrySetCanceled(ct));
        RhinoApp.InvokeOnUiThread((Action)(async () =>
        {
            try { ct.ThrowIfCancellationRequested(); result.TrySetResult(await action()); }
            catch (Exception ex) { result.TrySetException(ex); }
            finally { registration.Dispose(); }
        }));
        return result.Task;
    }
}
