using System.Text.Json;
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

    private static HostConnection Connect(AgentHost owner, string repository)
    {
        if (setupError is not null) throw new InvalidOperationException(setupError);
        var host = SingleScriptHost.Current;
        var doc = RhinoDoc.ActiveDoc ?? throw new InvalidOperationException("Open a Rhino document first.");
        if (host.Session.Repository != repository || host.BoundDocument != doc) throw new InvalidOperationException("Activate the rhDock document and repository before connecting.");
        var services = new DocumentServices(doc);
        var registry = ToolRegistry.Scan(typeof(HostTools).Assembly, services);
        var resources = ResourceRegistry.Scan(typeof(HostTools).Assembly, services);
        HostConnection? connection = null;
        void Check(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (RhinoDoc.ActiveDoc != doc || host.BoundDocument != doc || host.Session.Repository != repository)
            {
                connection?.Dispose();
                throw new InvalidOperationException("The connected document or repository changed. Reconnect explicitly.");
            }
        }
        connection = new HostConnection(Path.Combine(directory, "connections"), async (request, ct) =>
        {
            await OnUi(() => { Check(ct); return true; }, ct);
            if (request.Method == "tools") return Serialize(new { tools = registry.All.Where(t => !t.InPanelOnly).Select(t => new { t.Name, t.Description, readOnly = t.ReadOnly }) });
            if (request.Method == "describe")
                return registry.TryGet(request.Name ?? "", out var described) && !described.InPanelOnly
                    ? Serialize(Descriptor(described)) : throw new ArgumentException("Unknown tool: " + request.Name);
            if (request.Method == "resources") return Serialize(new { resources = resources.All.Select(r => new { r.Name, r.UriTemplate, r.Description, r.MimeType }) });
            if (request.Method == "resource")
            {
                var resource = resources.Match(request.Name ?? "", out var variables) ?? throw new ArgumentException("Unknown resource URI.");
                return await OnUiAsync(async () => { Check(ct); return Serialize(await resource.InvokeAsync(request.Name!, variables, services, ct)); }, ct);
            }
            if (request.Method != "call") throw new ArgumentException("Unknown request method.");
            if (!registry.TryGet(request.Name ?? "", out var tool) || tool.InPanelOnly) throw new ArgumentException("Unknown tool: " + request.Name);
            if (request.Arguments.ValueKind != JsonValueKind.Object) throw new ArgumentException("Tool arguments must be a JSON object.");
            var detail = request.Arguments.GetRawText();
            var atRevision = await OnUi(() => { Check(ct); return host.Revision; }, ct);
            if (!await owner.ApproveHostTool(tool.Name, detail, tool.ReadOnly, ct)) throw new InvalidOperationException("Host tool request was not approved.");
            await execution.WaitAsync(ct);
            try
            {
                owner.RecordHostTool(tool.Name, "InProgress", detail, request.Id!);
                var arguments = request.Arguments.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
                // Cocoa must process the document activation before a command such as Undo is dispatched.
                if (tool.Name is "run_command" or "run_python" or "run_csharp" or "close_doc")
                    await OnUi(() => { Check(ct); RhinoApp.SetFocusToMainWindow(doc); return true; }, ct);
                var result = await tool.InvokeAsync(arguments, services, ct, () =>
                {
                    Check(ct);
                    owner.CheckHostMode(tool.ReadOnly);
                    if (host.Revision != atRevision) throw new InvalidOperationException("The Script changed while this request was waiting. Read fresh state and request again.");
                    if (host.Running || host.ActivePick is not null || Command.InCommand()) throw new InvalidOperationException("Rhino or rhDock is busy. Wait until the current operation finishes.");
                    if (!tool.ReadOnly) DocumentSaveDiagnostics.Write("tool-begin:" + tool.Name + ":" + request.Id, doc);
                });
                var json = Serialize(result);
                owner.RecordHostTool(tool.Name, result.IsError ? "Failed" : "Completed", detail, request.Id!, json);
                await OnUi(() =>
                {
                    if (!tool.ReadOnly) DocumentSaveDiagnostics.Write("tool-end:" + tool.Name + ":" + request.Id, doc);
                    host.Refresh(); return true;
                }, ct);
                return json;
            }
            catch (Exception ex) { owner.RecordHostTool(tool.Name, "Failed", ex.Message, request.Id!); throw; }
            finally { execution.Release(); }
        });
        return connection;
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
