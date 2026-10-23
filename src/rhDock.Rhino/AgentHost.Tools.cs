using System.Text.Json;
using rhDock.Agents;

namespace rhDock.Rhino;

internal sealed partial class AgentHost
{
    internal static Func<AgentHost, string, HostConnection>? CreateHostConnection { get; set; }
    private HostConnection? hostConnection;
    private string? hostConnectionError;
    private readonly SemaphoreSlim hostApprovalGate = new(1, 1);
    private TaskCompletionSource<string?>? hostApproval;
    private PermissionAsked? hostPermission;
    private readonly HashSet<string> approvedHostTools = new(StringComparer.Ordinal);
    internal bool HostConnected => hostConnection?.Connected == true;

    public void ToggleHostConnection(string repository)
    {
        if (hostConnection?.Connected == true) DisconnectHost();
        else
        {
            try
            {
                hostConnection?.Dispose();
                hostConnection = CreateHostConnection?.Invoke(this, repository)
                    ?? throw new InvalidOperationException("Host tools are unavailable in this process.");
                hostConnectionError = null;
            }
            catch (Exception ex) { hostConnectionError = ex.Message; }
        }
        Changed();
    }

    internal static void RevokeHostConnections()
    {
        foreach (var host in OpenHosts()) { host.DisconnectHost(); host.Changed(); }
    }

    internal void CheckHostMode(bool readOnly)
    {
        lock (gate)
        {
            if (!HostConnected) throw new InvalidOperationException("Host access is disconnected.");
            if (!readOnly && mode == "plan" && Harnesses.Get(agentId).Features.Permissions) throw new InvalidOperationException("Plan mode permits reads only.");
        }
    }

    private void DisconnectHost()
    {
        hostConnection?.Dispose();
        hostConnection = null;
        lock (gate)
        {
            approvedHostTools.Clear();
            hostApproval?.TrySetResult(null);
            hostApproval = null;
            hostPermission = null;
            hostConnectionError = null;
        }
    }

    // A prompt is prepared when sent, so a queued message cannot carry a revoked connection's instructions.
    private AgentPrompt WithHostTools(AgentPrompt prompt)
    {
        lock (gate)
        {
            if (hostConnection is not { Connected: true } connection)
                return prompt with { Context = [.. prompt.Context, ("host-access", "rhDock host access is disconnected for this chat. Do not use connection IDs from earlier messages. The user controls rhDockConnect.")] };
            var path = Path.GetFileNameWithoutExtension(connection.FilePath);
            var instructions = $"This chat is connected to Rhino and rhDock. Use the bundled CLI: rhdock --connection {path} tools; "
                + $"rhdock --connection {path} describe TOOL; rhdock --connection {path} call TOOL --args-file JSON_FILE (or --args-file - to read the JSON from standard input). "
                + "Use resources and resource URI for RhinoAI resources. Arguments are JSON, never shell-interpolated source code. "
                + "Start with get_context and rhdock_state. Read each tool schema before calling. After source edits, use rhdock_reload then rhdock_run to verify. "
                + "Mutations require their reported revision and may wait for the user's approval. Never repeat an uncertain mutation with a new request id. "
                + "Image results contain an absolute path: open it with your image-viewing tool in this turn. If you cannot, state that you did not see the image. "
                + "To show the user an image (a result's image, or a PNG, JPEG, GIF or WebP file in the repository or a temp folder), put a Markdown image on its own line in your reply, "
                + "with the absolute path in angle brackets: ![Viewport](</absolute/path/image.jpg>); rhDock displays it inline. Image results include this line as `markdown`. "
                + "The connection is scoped to this chat's Rhino instance, document and repository; a revoked or changed target requires the user to reconnect. "
                + "Host access grants no approval to save, delete, execute scripts or Bake beyond the current permission mode.";
            return prompt with { Context = [.. prompt.Context, ("host-tools", instructions)] };
        }
    }

    internal async Task<bool> ApproveHostTool(string name, string detail, bool readOnly, CancellationToken cancellation)
    {
        await hostApprovalGate.WaitAsync(cancellation);
        try
        {
            TaskCompletionSource<string?> pending;
            lock (gate)
            {
                cancellation.ThrowIfCancellationRequested();
                if (!HostConnected) return false;
                if (readOnly) return true;
                var currentMode = Harnesses.Get(agentId).Features.Permissions ? mode : "all";
                if (currentMode == "plan") return false;
                if (currentMode == "all" || approvedHostTools.Contains(name)) return true;
                hostApproval = pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
                hostPermission = new PermissionAsked(sessionId ?? viewId, "host:" + Guid.NewGuid().ToString("N"), name, "Execute", detail);
            }
            Changed();
            var choice = await pending.Task.WaitAsync(cancellation);
            lock (gate)
            {
                cancellation.ThrowIfCancellationRequested();
                if (!HostConnected || mode == "plan") return false;
                if (choice == "AllowAlways") approvedHostTools.Add(name);
            }
            return choice is "AllowOnce" or "AllowAlways";
        }
        finally
        {
            lock (gate) { hostApproval = null; hostPermission = null; }
            hostApprovalGate.Release();
            Changed();
        }
    }

    internal void RecordHostTool(string name, string status, string detail, string callId, string? result = null)
    {
        lock (gate)
        {
            var item = items.FindLast(i => i.ToolCallId == "host:" + callId);
            if (item is null) Add(item = new Item { Id = NextId(), Kind = "tool", ToolCallId = "host:" + callId, Title = name, ToolKind = "Execute" });
            item.Status = status;
            item.Detail = detail;
            if (result is not null)
            {
                using var json = JsonDocument.Parse(result);
                if (json.RootElement.TryGetProperty("content", out var content))
                    foreach (var block in content.EnumerateArray())
                        if (block.TryGetProperty("type", out var type) && type.GetString() == "image"
                            && block.TryGetProperty("data", out var data) && block.TryGetProperty("mimeType", out var mime))
                        {
                            var id = NextId();
                            Remember(id, mime.GetString()!, data.GetString()!);
                            item.Images = [.. item.Images, id];
                        }
            }
        }
        Changed();
    }
}
