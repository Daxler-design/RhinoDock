using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

string? requestId = null, requestTarget = null;
var connections = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "rhDock", "host-tools", "connections");
static void CheckInstance(JsonNode connection, int pid)
{
    using var process = Process.GetProcessById(pid);
    if (process.HasExited || connection["pid"]?.GetValue<int>() != pid
        || connection["started"]?.GetValue<long>() != process.StartTime.ToUniversalTime().Ticks)
        throw new InvalidDataException("This Rhino instance has exited or its PID was reused. Discover instances again.");
}
static HttpClient Client(JsonNode connection, TimeSpan timeout)
{
    var endpoint = new Uri(connection["endpoint"]!.GetValue<string>());
    if (endpoint.Scheme != "http" || endpoint.Host != "localhost" || endpoint.AbsolutePath != "/" || endpoint.UserInfo.Length != 0)
        throw new InvalidDataException("Only a local rhDock connection is allowed.");
    var client = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = false })
        { BaseAddress = endpoint, Timeout = timeout };
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", connection["token"]!.GetValue<string>());
    return client;
}
static async Task<JsonNode> Status(HttpClient client)
{
    using var response = await client.PostAsync("", new StringContent("{\"method\":\"status\"}", Encoding.UTF8, "application/json"));
    response.EnsureSuccessStatusCode();
    return JsonNode.Parse(await response.Content.ReadAsStringAsync()) ?? throw new InvalidDataException("Host returned no status.");
}
try
{
    if (args is ["--help"] or [])
    {
        Console.WriteLine("rhdock instances\nrhdock --pid <RhinoPID> status | tools | describe <name> | call <name> [--args <json> | --args-file <file, or - for standard input>] [--request-id <uuid>] [--target <status-target>] [--source <view-id>] | resources | resource <uri> [--target <status-target>]\n--connection <id-or-file> remains available for explicit credentials.");
        return 0;
    }
    if (args is ["instances"])
    {
        var instances = new JsonArray();
        foreach (var path in Directory.Exists(connections) ? Directory.EnumerateFiles(connections, "pid-*.json") : [])
        {
            try
            {
                var candidate = JsonNode.Parse(await File.ReadAllTextAsync(path)) ?? throw new InvalidDataException("Invalid instance descriptor.");
                CheckInstance(candidate, candidate["pid"]?.GetValue<int>() ?? throw new InvalidDataException("Missing instance PID."));
                using var probe = Client(candidate, TimeSpan.FromSeconds(2));
                instances.Add(await Status(probe));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException
                or ArgumentException or HttpRequestException or TaskCanceledException or System.ComponentModel.Win32Exception) { }
        }
        Console.WriteLine(new JsonObject { ["instances"] = instances }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
    if (args.Length < 3 || args[0] is not ("--pid" or "--connection"))
        throw new ArgumentException("Specify --pid <RhinoPID> or --connection <id-or-file>. Use --help for commands.");
    int? pid = null;
    string file;
    if (args[0] == "--pid")
    {
        if (!int.TryParse(args[1], out var selected) || selected <= 0) throw new ArgumentException("Invalid Rhino PID.");
        pid = selected;
        file = Path.Combine(connections, $"pid-{selected}.json");
    }
    else file = Guid.TryParse(args[1], out var connectionId)
        ? Path.Combine(connections, connectionId.ToString("N") + ".json") : Path.GetFullPath(args[1]);
    var connection = JsonNode.Parse(await File.ReadAllTextAsync(file)) ?? throw new InvalidDataException("Invalid connection file.");
    if (pid is { } selectedPid) CheckInstance(connection, selectedPid);
    using var client = Client(connection, TimeSpan.FromMinutes(10));
    var method = args[2];
    if (method is not ("status" or "tools" or "describe" or "call" or "resources" or "resource")) throw new ArgumentException("Unknown command. Use --help.");
    string? name = null;
    var at = 3;
    if (method is "describe" or "call" or "resource")
    {
        if (args.Length <= at) throw new ArgumentException("This command requires a tool name or resource URI.");
        name = args[at++];
    }
    var arguments = JsonNode.Parse("{}");
    var id = Guid.NewGuid().ToString();
    var suppliedArgs = false;
    string? target = null, source = null;
    while (at < args.Length)
    {
        if (method is not ("call" or "resource") || at + 1 >= args.Length) throw new ArgumentException("Unexpected command arguments.");
        var flag = args[at++]; var value = args[at++];
        if (flag == "--target") target = value;
        else if (flag == "--source" && method == "call") source = value;
        else if (method != "call") throw new ArgumentException("Unexpected resource flag: " + flag);
        else if (flag == "--request-id") { if (!Guid.TryParse(value, out _)) throw new ArgumentException("Invalid request UUID."); id = value; }
        else if (flag is "--args" or "--args-file")
        {
            if (suppliedArgs) throw new ArgumentException("Supply arguments once.");
            suppliedArgs = true;
            // "--args-file -" reads standard input, as most CLIs accept; empty input is reported plainly instead of as a parser error.
            var supplied = flag == "--args" ? value : value == "-" ? await Console.In.ReadToEndAsync() : await File.ReadAllTextAsync(value);
            if (string.IsNullOrWhiteSpace(supplied)) throw new ArgumentException(flag == "--args" ? "--args is empty; pass a JSON object such as {}." : "The arguments file is empty; write a JSON object such as {} to it.");
            arguments = JsonNode.Parse(supplied);
            if (arguments is not JsonObject) throw new ArgumentException("Tool arguments must be a JSON object.");
        }
        else throw new ArgumentException("Unknown flag: " + flag);
    }
    requestId = id;
    if ((method is "call" or "resource") && target is null)
        target = (await Status(client))["target"]?.GetValue<string>() ?? throw new InvalidDataException("Host did not report its current target.");
    var payload = new JsonObject { ["method"] = method, ["name"] = name, ["arguments"] = arguments, ["id"] = id, ["target"] = target, ["source"] = source };
    requestTarget = target;
    using var response = await client.PostAsync("", new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"));
    var text = await response.Content.ReadAsStringAsync();
    var result = JsonNode.Parse(text) ?? throw new InvalidDataException("Host returned no result.");
    if (result["content"] is JsonArray content)
        foreach (var item in content.OfType<JsonObject>())
            if (item["type"]?.GetValue<string>() == "image" && item["data"] is JsonValue data)
            {
                var mime = item["mimeType"]?.GetValue<string>();
                var extension = mime switch { "image/png" => ".png", "image/jpeg" => ".jpg", _ => throw new InvalidDataException("Unsupported image type: " + mime) };
                var image = Path.Combine(Path.GetDirectoryName(file)!, "image-" + Guid.NewGuid().ToString("N") + extension);
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                await using (var stream = new FileStream(image, options)) await stream.WriteAsync(Convert.FromBase64String(data.GetValue<string>()));
                item.Remove("data"); item["path"] = image;
                item["markdown"] = $"![{(name?.Contains("viewport", StringComparison.OrdinalIgnoreCase) == true ? "Viewport" : "Image")}](<{image}>)";
                item["instruction"] = "Open this absolute path with your image-viewing tool now; a path alone is not visual evidence. If unavailable, report that you have not seen the image. To show it to the user, put the markdown line on its own line in your reply.";
            }
    result["requestId"] = id;
    if (target is not null) result["target"] = target;
    Console.WriteLine(result.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    return response.IsSuccessStatusCode && result["isError"]?.GetValue<bool>() != true ? 0 : 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new { error = ex.Message, requestId, target = requestTarget, note = "If a mutation timed out, check host state before retrying. Reuse --request-id for the identical request." }));
    return 1;
}
