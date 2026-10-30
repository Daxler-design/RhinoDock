using System.Text.Json;
using rhDock.Agents;

namespace rhDock.Rhino;

internal sealed partial class AgentHost
{
    internal static Func<HostConnection>? CreateHostConnection { get; set; }
    private static HostConnection? hostConnection;
    private static string? hostConnectionError;
    internal bool HostConnected => hostConnection?.Connected == true;

    public void ToggleHostConnection()
    {
        if (HostConnected) RevokeHostConnections();
        else
        {
            try
            {
                hostConnection?.Dispose();
                hostConnection = CreateHostConnection?.Invoke()
                    ?? throw new InvalidOperationException("Host tools are unavailable in this process.");
                hostConnectionError = null;
            }
            catch (Exception ex) { hostConnectionError = ex.Message; }
        }
        foreach (var host in OpenHosts()) host.Changed();
        Changed();
    }

    internal static void RevokeHostConnections()
    {
        hostConnection?.Dispose();
        hostConnection = null;
        hostConnectionError = null;
        foreach (var host in OpenHosts()) host.Changed();
    }

    // A prompt is prepared when sent, so a queued message cannot carry a revoked connection's instructions.
    private AgentPrompt WithHostTools(AgentPrompt prompt)
    {
        lock (gate)
        {
            if (!HostConnected)
                return prompt with { Context = [.. prompt.Context, ("host-access", "rhDock host access is disabled for this Rhino instance. Do not use earlier connection instructions. The user controls rhDockConnect.")] };
            var instructions = $"rhDockConnect is enabled for Rhino PID {Environment.ProcessId}. Use the bundled CLI: rhdock --pid {Environment.ProcessId} tools; "
                + $"rhdock --pid {Environment.ProcessId} describe TOOL; rhdock --pid {Environment.ProcessId} call TOOL --source {viewId} --args-file JSON_FILE (or --args-file - to read the JSON from standard input). "
                + "Use resources and resource URI for RhinoAI resources. Arguments are JSON, never shell-interpolated source code. "
                + "Begin with status and retain its target. Pass --target TARGET on subsequent call/resource commands for that task; on a target error, inspect fresh state before proceeding. Start with get_context and rhdock_state. Read each tool schema before calling. After source edits, use rhdock_reload then rhdock_run to verify. "
                + "Mutations require their reported revision. The CLI validates the current target before dispatch. Never repeat an uncertain mutation with a new request id. "
                + "Image results contain an absolute path: open it with your image-viewing tool in this turn. If you cannot, state that you did not see the image. "
                + "To show the user an image (a result's image, or a PNG, JPEG, GIF or WebP file in the repository or a temp folder), put a Markdown image on its own line in your reply, "
                + "with the absolute path in angle brackets: ![Viewport](</absolute/path/image.jpg>); rhDock displays it inline. Image results include this line as `markdown`. "
                + $"This instance connection is shared across chats and harnesses. Use rhdock --pid {Environment.ProcessId} status to inspect its current document and repository; read fresh state after a target change. "
                + "The instance switch enables Rhino tool access independently of harness permissions. Follow the user's instructions and your harness mode for all actions.";
            return prompt with { Context = [.. prompt.Context, ("host-tools", instructions)] };
        }
    }

    internal static void RecordInstanceTool(string? source, string name, string status, string detail, string callId, string? result = null)
    {
        HostLog.Write($"host-tool pid={Environment.ProcessId} source={JsonSerializer.Serialize(source ?? "external")} request={callId} tool={name} status={status}");
        var owner = OpenHosts().FirstOrDefault(h => h.viewId == source && !h.disposed);
        owner?.RecordHostTool(name, status, detail, callId, result);
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
