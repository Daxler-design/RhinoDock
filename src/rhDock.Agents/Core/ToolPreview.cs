using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using static rhDock.Agents.Json;

namespace rhDock.Agents;

/// <summary>
/// One name for the same tool across agents (MonoCode's core/preview.ts): Claude's <c>Edit</c>, OpenCode's <c>edit</c>, and Pi's
/// <c>edit</c> all become an Edit with a short title. Kinds are those of <see cref="ToolCall"/>.
/// </summary>
internal static class ToolPreview
{
    public static (string Kind, string Title, string? File) Describe(string name, JsonElement input)
    {
        var tool = name.ToLowerInvariant();
        // MCP tools: Claude Code names them mcp__server__tool; OpenCode 2 runs them from code, execute { code: "tools.server.tool(…)" }.
        if (tool.StartsWith("mcp__", StringComparison.Ordinal) && name.Split("__") is [_, var server, .. var rest] && rest.Length > 0)
            return ("Other", Mcp(server, string.Join("__", rest)), null);
        if (tool == "execute" && Str(input, "code") is { } code)
        {
            var calls = Regex.Matches(code, @"tools\.([\w-]+)\.([\w-]+)\s*\(").Select(m => Mcp(m.Groups[1].Value, m.Groups[2].Value)).Distinct().ToArray();
            if (calls.Length > 0) return ("Other", string.Join(", ", calls), null);
            return Regex.Match(code, @"search\(\s*\{\s*query:\s*[""'`]([^""'`]+)") is { Success: true } search ? ("Search", $"Search MCP tools for {search.Groups[1].Value}", null) : ("Other", "Run MCP tool code", null);
        }
        var file = First(input, "filePath", "file_path", "path", "file", "notebook_path");
        var kind = tool switch
        {
            "read" or "read_file" or "readfile" or "view" => "Read",
            "edit" or "multiedit" or "write" or "write_file" or "create" or "patch" or "apply_patch" or "notebookedit" or "searchreplace" or "str_replace" => "Edit",
            "delete" or "remove" or "delete_file" => "Delete",
            "bash" or "shell" or "exec" or "exec_command" or "execute" or "run_terminal_command" or "terminal" => "Execute",
            "grep" or "search" or "rg" or "codesearch" or "glob" or "find" or "ls" or "list" or "list_dir" or "listdir" or "toolsearch" => "Search",
            "webfetch" or "web_fetch" or "fetch" or "websearch" or "web_search" => "Fetch",
            "task" or "agent" or "subagent" or "todowrite" or "todo_write" or "todoread" or "taskcreate" or "taskupdate" or "tasklist" or "update_plan" or "exitplanmode" => "Think",
            _ => "Other",
        };
        var title = tool switch
        {
            "read" or "read_file" or "readfile" or "view" => $"Read {Name(file)}",
            "write" or "write_file" or "create" => $"Write {Name(file)}",
            "delete" or "remove" or "delete_file" => $"Delete {Name(file)}",
            _ when kind == "Edit" => file is null ? "Edit files" : $"Edit {Name(file)}",
            _ when kind == "Execute" => First(input, "description", "command", "cmd") ?? "Run a command",
            "grep" or "search" or "rg" or "codesearch" => $"Search for {First(input, "pattern", "query")}",
            "glob" or "find" => $"Find {First(input, "pattern", "query")}",
            "ls" or "list" or "list_dir" or "listdir" => "List " + (file is null ? "files" : Name(file)),
            "webfetch" or "web_fetch" or "fetch" => $"Fetch {Str(input, "url")}",
            "websearch" or "web_search" => $"Search the web for {Str(input, "query")}",
            "task" or "agent" or "subagent" => First(input, "description", "prompt") ?? "Subagent",
            // Claude Code loads deferred tools with "select:Name,Other".
            "toolsearch" => First(input, "query") is { } wanted ? wanted.StartsWith("select:", StringComparison.Ordinal) ? $"Load the {wanted[7..].Replace(",", ", ")} tool" : $"Search tools for {wanted}" : "Search tools",
            "exitplanmode" => "Propose the plan",
            _ when kind == "Think" => "Update the plan",
            "askuserquestion" or "question" => "Ask a question",
            "skill" => $"Load the {First(input, "name", "skill", "command")} skill",
            _ => name,
        };
        return (kind, title.Trim(), file);
    }

    /// <summary>An MCP tool call's title for every agent, such as "rhino: run_python".</summary>
    public static string Mcp(string server, string tool) => $"{server}: {tool}";

    public static string Name(string? path) => path is null ? "" : Path.GetFileName(path.TrimEnd('/', '\\'));
}
