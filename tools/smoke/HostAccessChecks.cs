using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using rhDock.Agents;
using rhDock.Core;
using rhDock.Rhino;
using rhDock.RhinoAI.Server;
using rhDock.RhinoAI.Tools;

internal static class HostAccessChecks
{
    public static async Task Run(string cli)
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        var folder = Path.Combine(Path.GetTempPath(), "rhdock-host-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var registry = ToolRegistry.Scan(typeof(HostAccessChecks).Assembly, new Services());
            Check(registry.TryGet("fixture_json", out var jsonTool), "Fixture tool was not discovered.");
            Check(!jsonTool.InputSchema.GetProperty("properties").GetProperty("value").TryGetProperty("type", out _), "Raw JSON input was incorrectly constrained to an object.");
            var nullResult = await jsonTool.InvokeAsync(new Dictionary<string, JsonElement> { ["value"] = JsonSerializer.Deserialize<JsonElement>("null") }, new Services(), default);
            Check(!nullResult.IsError && nullResult.Content.Any(c => c.Text == "Null"), "The real dispatcher rejected JSON null used to clear an input.");
            var missing = await jsonTool.InvokeAsync(new Dictionary<string, JsonElement>(), new Services(), default);
            Check(missing.IsError, "The dispatcher accepted a missing required input.");
            var validated = false;
            await jsonTool.InvokeAsync(new Dictionary<string, JsonElement> { ["value"] = JsonSerializer.Deserialize<JsonElement>("7") }, new Services(), default, () => validated = true);
            Check(validated, "The dispatcher skipped target validation on the UI thread.");
            Check(registry.TryGet("fixture_layout", out var layoutTool), "Layout fixture was not discovered.");
            var layoutResult = await layoutTool.InvokeAsync(new Dictionary<string, JsonElement>
            {
                ["section"] = JsonSerializer.Deserialize<JsonElement>("{\"groups\":[{\"name\":\"Settings\",\"ports\":[\"values\",\"factor\"],\"previewOff\":true}],\"ungrouped\":[\"enabled\"]}")
            }, new Services(), default);
            Check(!layoutResult.IsError, "Typed layout JSON failed to bind.");
            var boundLayout = JsonSerializer.Deserialize<SectionLayout>(layoutResult.Content.Single().Text!, McpSerializer.Options)!;
            Check(boundLayout.Groups[0].PreviewOff == true && boundLayout.Groups[0].Ports.SequenceEqual(new[] { "values", "factor" })
                && boundLayout.Ungrouped.SequenceEqual(new[] { "enabled" }), "Layout binding lost group Preview or Port order.");
            Console.WriteLine("PASS: actual tool discovery, JSON schema/null binding, required inputs, and execution-time target validation.");
            var executions = 0;
            var arguments = JsonSerializer.SerializeToElement(new { value = 3 });
            using var connection = new HostConnection(folder, async (request, ct) =>
            {
                if (request.Method == "status") return "{\"target\":\"fixture\"}";
                if (request.Method == "call") { request.CheckTarget("fixture"); Interlocked.Increment(ref executions); }
                await Task.Delay(25, ct);
                if (request.Name == "fails") throw new InvalidOperationException("Fixture failure");
                if (request.Name == "image") return "{\"content\":[{\"type\":\"image\",\"mimeType\":\"image/png\",\"data\":\"iVBORw0KGgo=\"},{\"type\":\"text\",\"text\":\"camera metadata\"}]}";
                return "{\"content\":[{\"type\":\"text\",\"text\":\"done\"}]}";
            });
            using var credentials = JsonDocument.Parse(File.ReadAllText(connection.FilePath));
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri(connection.Endpoint) };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credentials.RootElement.GetProperty("token").GetString());
            Task<HttpResponseMessage> Post(HostRequest request) => client.PostAsync("", new StringContent(JsonSerializer.Serialize(request, HostConnection.Json), Encoding.UTF8, "application/json"));
            using (var denied = await new HttpClient(new HttpClientHandler { UseProxy = false }).PostAsync(connection.Endpoint, new StringContent("{}"))) Check(denied.StatusCode == HttpStatusCode.Forbidden, "Missing authorization was accepted.");
            client.DefaultRequestHeaders.Add("Origin", "http://localhost");
            using (var denied = await Post(new("tools", Arguments: arguments))) Check(denied.StatusCode == HttpStatusCode.Forbidden, "Browser-origin request was accepted.");
            client.DefaultRequestHeaders.Remove("Origin");
            var id = Guid.NewGuid().ToString();
            var duplicate = new HostRequest("call", "mutate", arguments, id, Target: "fixture");
            var replies = await Task.WhenAll(Post(duplicate), Post(duplicate));
            foreach (var reply in replies) { Check(reply.IsSuccessStatusCode, "Repeated identical call failed."); reply.Dispose(); }
            using (var formatted = await Post(duplicate with { Id = Guid.Parse(id).ToString("N").ToUpperInvariant() })) Check(formatted.IsSuccessStatusCode, "A UUID spelling change lost deduplication.");
            Check(executions == 1, "An identical mutation ran twice.");
            using (var changed = await Post(duplicate with { Name = "other" })) Check(changed.StatusCode == HttpStatusCode.BadRequest, "A reused request ID accepted different arguments.");
            Check(executions == 1, "A conflicting request ID executed a mutation.");
            var failure = duplicate with { Name = "fails", Id = Guid.NewGuid().ToString() };
            using (var failed = await Post(failure)) Check(!failed.IsSuccessStatusCode, "Thrown tool failure was reported as success.");
            using (var failedAgain = await Post(failure)) Check(!failedAgain.IsSuccessStatusCode, "Cached failure was not preserved.");
            Check(executions == 2, "A failed mutation was re-executed on retry.");
            for (var i = 0; i < 300; i++)
            {
                using var response = await Post(new("call", "mutate", arguments, Guid.NewGuid().ToString(), Target: "fixture"));
                Check(response.IsSuccessStatusCode, "Connection stopped accepting new calls before 300 requests.");
            }
            using (var retry = await Post(duplicate)) Check(retry.IsSuccessStatusCode, "Old result was lost after 300 calls.");
            Check(executions == 302, "An old mutation was executed twice after the former call cap.");
            using (var mismatch = await Post(duplicate with { Name = "different" })) Check(!mismatch.IsSuccessStatusCode, "Old request id accepted different arguments.");
            using (var retry = await Post(failure)) Check(!retry.IsSuccessStatusCode, "Old failure was lost after 300 calls.");
            Check(executions == 302, "An old failure was executed twice.");
            Console.WriteLine("PASS: loopback authorization, browser-origin rejection, and at-most-once mutation/failure retries.");

            var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            info.Environment["DOTNET_ROLL_FORWARD"] = "Major";
            foreach (var arg in new[] { Path.GetFullPath(cli), "--connection", connection.FilePath, "call", "image", "--args", "{}" }) info.ArgumentList.Add(arg);
            using (var process = Process.Start(info)!)
            {
                var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                Check(process.ExitCode == 0, "CLI request failed: " + await error);
                using var result = JsonDocument.Parse(await output);
                var content = result.RootElement.GetProperty("content");
                var image = content[0].GetProperty("path").GetString()!;
                Check(Path.IsPathFullyQualified(image) && File.ReadAllBytes(image).SequenceEqual(Convert.FromBase64String("iVBORw0KGgo=")), "CLI lost the image bytes.");
                Check(!content[0].TryGetProperty("data", out _) && content[1].GetProperty("text").GetString() == "camera metadata", "CLI lost mixed image/text metadata.");
                Check(result.RootElement.TryGetProperty("requestId", out _), "CLI did not return its retry ID.");
            }
            Console.WriteLine("PASS: real CLI round trip preserves image bytes, metadata, absolute artifact paths, and request IDs.");

            async Task<(int Exit, string Output, string Error)> Cli(params string[] command)
            {
                var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                start.Environment["DOTNET_ROLL_FORWARD"] = "Major";
                start.ArgumentList.Add(Path.GetFullPath(cli));
                foreach (var arg in command) start.ArgumentList.Add(arg);
                using var child = Process.Start(start)!;
                var output = child.StandardOutput.ReadToEndAsync();
                var error = child.StandardError.ReadToEndAsync();
                try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
                finally { if (!child.HasExited) child.Kill(entireProcessTree: true); }
                return (child.ExitCode, await output, await error);
            }
            var instanceDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "rhDock", "host-tools", "connections");
            using (var instance = new HostConnection(instanceDirectory, (request, _) =>
            {
                if (request.Method == "status") return Task.FromResult(JsonSerializer.Serialize(new { pid = Environment.ProcessId, target = "current-document" }));
                if (request.Method == "call") request.CheckTarget("current-document");
                return Task.FromResult(JsonSerializer.Serialize(new { source = request.Source, content = Array.Empty<object>() }));
            }, publishInstance: true))
            {
                var selectedPid = Environment.ProcessId.ToString();
                var discovered = await Cli("instances");
                Check(discovered.Exit == 0, "Instance discovery failed: " + discovered.Error);
                using var list = JsonDocument.Parse(discovered.Output);
                Check(list.RootElement.GetProperty("instances").EnumerateArray().Any(i => i.GetProperty("pid").GetInt32() == Environment.ProcessId), "Live instance was not discovered.");
                Check(!discovered.Output.Contains("token", StringComparison.Ordinal), "Discovery exposed a credential.");
                var status = await Cli("--pid", selectedPid, "status");
                Check(status.Exit == 0 && status.Output.Contains("current-document", StringComparison.Ordinal), "PID status failed: " + status.Error);
                var call = await Cli("--pid", selectedPid, "call", "mutate", "--source", "fixture-chat");
                Check(call.Exit == 0 && call.Output.Contains("fixture-chat", StringComparison.Ordinal), "PID call lost target or source: " + call.Error);
                var stale = await Cli("--pid", selectedPid, "call", "mutate", "--target", "old-document");
                Check(stale.Exit != 0, "Explicit stale target was accepted.");
                var saved = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(instance.FilePath))!;
                saved["started"] = 0L;
                File.WriteAllText(instance.FilePath, saved.ToJsonString());
                var reused = await Cli("--pid", selectedPid, "status");
                Check(reused.Exit != 0 && reused.Error.Contains("PID was reused", StringComparison.Ordinal), "A stale PID descriptor was accepted.");
                instance.Dispose();
                var revoked = await Cli("--pid", selectedPid, "status");
                Check(revoked.Exit != 0, "Disabled instance remained available through its PID.");
            }
            Console.WriteLine("PASS: real CLI instance discovery, PID routing, automatic/explicit targets, source labels, PID reuse rejection and revocation.");

            using var other = new HostConnection(folder, (_, _) => Task.FromResult("{}"));
            using (var crossChat = await client.PostAsync(other.Endpoint, new StringContent("{}"))) Check(crossChat.StatusCode == HttpStatusCode.Forbidden, "A different capability's credential was accepted.");
            var path = connection.FilePath;
            connection.Dispose();
            Check(!connection.Connected && !File.Exists(path) && connection.Cancellation.IsCancellationRequested, "Disconnect did not revoke credentials and pending work.");

            using (var owner = new HostFixture(folder))
            {
                var another = new AgentHost();
                another.SetMode("plan");
                Check(another.HostConnected, "A different chat did not share the instance grant.");
                owner.Host.Stop();
                Check(another.HostConnected && owner.Connection.Connected, "Stopping a harness revoked the instance grant.");
                another.ToggleHostConnection();
                Check(!another.HostConnected && !owner.Host.HostConnected, "Disabling the instance left another chat connected.");
                another.Stop();
                owner.Host.ToggleHostConnection();
                Check(owner.Host.HostConnected, "Re-enabling the instance failed.");
                AgentHost.Shutdown();
                Check(!owner.Host.HostConnected, "Shutdown retained an instance grant.");
            }
            Console.WriteLine("PASS: instance access shared across chats and permission modes; harness disposal preserves access; switch revokes it globally.");

            var target = "document-a";
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var targetedExecutions = 0;
            using (var targeted = new HostConnection(folder, async (request, ct) =>
            {
                request.CheckTarget(target);
                entered.TrySetResult();
                await resume.Task.WaitAsync(ct);
                request.CheckTarget(target);
                Interlocked.Increment(ref targetedExecutions);
                return "{}";
            }))
            {
                using var credential = JsonDocument.Parse(File.ReadAllText(targeted.FilePath));
                using var caller = new HttpClient(new HttpClientHandler { UseProxy = false });
                caller.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential.RootElement.GetProperty("token").GetString());
                var request = new HostRequest("call", "mutate", arguments, Guid.NewGuid().ToString(), Target: target);
                Task<HttpResponseMessage> Send(HostRequest value) => caller.PostAsync(targeted.Endpoint,
                    new StringContent(JsonSerializer.Serialize(value, HostConnection.Json), Encoding.UTF8, "application/json"));
                var queued = Send(request);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                target = "document-b";
                resume.SetResult();
                using (var stale = await queued) Check(!stale.IsSuccessStatusCode, "Queued work crossed documents.");
                using (var retry = await Send(request)) Check(!retry.IsSuccessStatusCode, "Retry lost the rejected target outcome.");
                using (var missingTarget = await Send(request with { Id = Guid.NewGuid().ToString(), Target = null }))
                    Check(!missingTarget.IsSuccessStatusCode, "An unbound call was accepted.");
                using (var fresh = await Send(request with { Id = Guid.NewGuid().ToString(), Target = target }))
                    Check(fresh.IsSuccessStatusCode, "New document required reconnecting.");
                Check(targetedExecutions == 1 && targeted.Connected, "Target change revoked the grant or executed stale work.");
            }
            Console.WriteLine("PASS: missing/stale targets and queued cross-document calls rejected; fresh work uses the same grant.");

            var cardHost = AgentHost.Current;
            var source = JsonSerializer.SerializeToElement(cardHost.Snapshot()).GetProperty("viewId").GetString();
            AgentHost.RecordInstanceTool(source, "image", "Completed", "{}", "source-test",
                "{\"content\":[{\"type\":\"image\",\"mimeType\":\"image/png\",\"data\":\"iVBORw0KGgo=\"}]}");
            var card = JsonSerializer.SerializeToElement(cardHost.Snapshot()).GetProperty("items").EnumerateArray().Single(i => i.GetProperty("callId").GetString() == "host:source-test");
            Check(card.GetProperty("images").GetArrayLength() == 1, "Instance tool result lost its source conversation image.");
            Check(JsonSerializer.SerializeToElement(new AgentHost().Snapshot()).GetProperty("items").GetArrayLength() == 0, "Tool record leaked to a different conversation.");
            AgentHost.Shutdown();
            Console.WriteLine("PASS: source labels route tool/image cards to their conversation without sharing transcripts.");

            using (var fake = new AgentHostChecks.Fake(Path.Combine(folder, "source.txt")))
            {
                var host = new AgentHost(_ => fake);
                HostConnection? grant = null;
                AgentHost.CreateHostConnection = () => grant = new HostConnection(Path.Combine(folder, "grant"), (_, _) => Task.FromResult("{}"));
                async Task Until(Func<bool> condition)
                {
                    for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(20);
                    Check(condition(), "Prompt instruction fixture timed out.");
                }
                try
                {
                    host.Connect(folder);
                    await Until(() => JsonSerializer.SerializeToElement(host.Snapshot()).GetProperty("status").GetString() == "ready");
                    host.ToggleHostConnection();
                    using var credential = JsonDocument.Parse(File.ReadAllText(grant!.FilePath));
                    host.Prompt(folder, "hold", [("selection", "chosen objects")], [], []);
                    await Until(() => fake.Holding);
                    Check(fake.LastPrompt!.Context.Any(c => c.Name == "host-tools") && fake.LastPrompt.Context.Any(c => c.Name == "selection"), "The shared prompt path lost connection instructions or user context.");
                    Check(!fake.LastPrompt.Tagged().Contains(credential.RootElement.GetProperty("token").GetString()!, StringComparison.Ordinal), "A credential leaked into model context.");
                    host.Prompt(folder, "queued after disconnect", [], [], []);
                    host.ToggleHostConnection();
                    fake.Finish.TrySetResult(new TurnResult("end_turn", null));
                    await Until(() => fake.LastPrompt?.Text == "queued after disconnect");
                    Check(!fake.LastPrompt!.Context.Any(c => c.Name == "host-tools"), "A queued message retained revoked connection instructions.");
                }
                finally { host.Stop(); AgentHost.RevokeHostConnections(); AgentHost.CreateHostConnection = null; }
            }
            Console.WriteLine("PASS: shared harness prompts receive discovery without tokens; queued prompts respect disconnect.");

            object? Parse(string type, PortAccess access, string json) => HostInput.Parse(type, access, JsonSerializer.Deserialize<JsonElement>(json));
            Check(Parse("System.Double", PortAccess.Item, "1") is double d && d == 1, "JSON integer did not fit a double Port.");
            Check(Parse("System.Int32", PortAccess.List, "[1,2]") is int[] ints && ints.SequenceEqual(new[] { 1, 2 }), "List was not preserved whole.");
            Check(Parse("System.String", PortAccess.Item, "\"\"") is "", "An empty string was cleared.");
            foreach (var fixture in new[] { ("System.Int32", PortAccess.Item, "1.5"), ("System.Double", PortAccess.Item, "\"NaN\""), ("System.Boolean", PortAccess.Item, "\"false\""), ("System.Int32", PortAccess.List, "[1,\"2\"]"), ("Script.OwnType", PortAccess.Item, "{}") })
            {
                try { Parse(fixture.Item1, fixture.Item2, fixture.Item3); throw new Exception("Invalid input was accepted."); }
                catch (ArgumentException) { }
            }
            Console.WriteLine("PASS: strict scalar/list input boundary, empty strings, and Script-type rejection.");
        }
        finally
        {
            // Connection outcome cleanup can still be draining cancelled requests.
            for (var attempt = 0; ; attempt++)
            {
                try { if (Directory.Exists(folder)) Directory.Delete(folder, true); break; }
                catch (IOException) when (attempt < 20) { await Task.Delay(100); }
            }
        }
    }

    private sealed class Services : IServiceProvider { public object? GetService(Type type) => null; }

    [McpServerToolType]
    private static class BindingFixture
    {
        [McpServerTool("fixture_json", "JSON fixture", true, false)]
        public static IToolResult Echo(JsonElement value) => ToolResult.Success(ContentBlock.CreateText(value.ValueKind.ToString()));

        [McpServerTool("fixture_layout", "Layout fixture", false, false)]
        public static IToolResult Layout(SectionLayout section) => ToolResult.Success(ContentBlock.CreateJson(section));
    }

    private sealed class HostFixture : IDisposable
    {
        internal AgentHost Host { get; } = new();
        internal HostConnection Connection { get; }
        public HostFixture(string folder)
        {
            HostConnection? connection = null;
            AgentHost.CreateHostConnection = () => connection = new HostConnection(folder, (_, _) => Task.FromResult("{}"));
            Host.ToggleHostConnection();
            Connection = connection ?? throw new Exception("Fixture host did not connect.");
        }
        public void Dispose() { Host.Stop(); AgentHost.RevokeHostConnections(); AgentHost.CreateHostConnection = null; }
    }
}
