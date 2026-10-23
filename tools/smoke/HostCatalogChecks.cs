using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

internal static class HostCatalogChecks
{
    // External tool contract of RhinoAI 46756c0, compiled for R8, independent of rhDock's dispatcher.
    private static readonly string[] Rhino8 = ["close_doc", "g1_apply_graph", "g1_clear_canvas", "g1_connect", "g1_connect_many", "g1_delete_component", "g1_describe_component", "g1_get_canvas_graph", "g1_place_component", "g1_place_slider", "g1_search_components", "g1_solve_graph", "g1_start", "get_commands", "get_context", "get_selection", "get_viewport_image", "list_objects", "open_doc", "run_command", "run_csharp", "run_python", "save_doc", "set_camera", "set_layer_material", "set_selection", "zoom_to_layer", "zoom_to_object"];
    private static readonly string[] Host = ["rhdock_state", "rhdock_scripts", "rhdock_select_script", "rhdock_set_input", "rhdock_set_picks", "rhdock_run", "rhdock_reload", "rhdock_outputs", "rhdock_preview", "rhdock_preview_all", "rhdock_set_layout", "rhdock_request_pick", "rhdock_bake", "rhdock_solve", "rhdock_rhino_type"];

    public static void Run(string plugin)
    {
        using var stream = File.OpenRead(plugin);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var resources = 0;
        foreach (var handle in metadata.CustomAttributes)
        {
            var attribute = metadata.GetCustomAttribute(handle);
            if (attribute.Constructor.Kind != HandleKind.MethodDefinition) continue;
            var constructor = metadata.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor);
            var type = metadata.GetTypeDefinition(constructor.GetDeclaringType());
            if (metadata.GetString(type.Name) == "McpServerResourceAttribute") resources++;
            if (metadata.GetString(type.Name) != "McpServerToolAttribute") continue;
            var blob = metadata.GetBlobReader(attribute.Value);
            if (blob.ReadUInt16() != 1) throw new Exception("Invalid tool attribute.");
            var name = blob.ReadSerializedString() ?? throw new Exception("Tool name is missing.");
            if (!names.Add(name)) throw new Exception("Duplicate tool: " + name);
        }
        var expected = Rhino8.Concat(Host).ToHashSet(StringComparer.Ordinal);
        if (!names.SetEquals(expected)) throw new Exception("Compiled tool catalog mismatch. Missing: " + string.Join(", ", expected.Except(names)) + "; unexpected: " + string.Join(", ", names.Except(expected)));
        if (resources != 3) throw new Exception("The compiled plugin must include all three upstream external resources.");
        Console.WriteLine($"PASS: compiled plugin contains all {Rhino8.Length} upstream Rhino 8 external tools, three resources and {Host.Length} rhDock tools, without loading native Rhino.");
    }
}
