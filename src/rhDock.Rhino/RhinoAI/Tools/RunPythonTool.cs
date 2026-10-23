// Adapted from McNeel RhinoAI, MIT; see UPSTREAM.md.
using System;
using System.Linq;
using System.Text.Json;
using System.Collections;
using System.ComponentModel;
using System.Collections.Generic;

using static rhDock.RhinoAI.Tools.ToolResult;
using rhDock.RhinoAI.Server;

using Rhino;

using rhDock.RhinoAI.ScriptProjects;

namespace rhDock.RhinoAI.Tools;

[McpServerToolType]
internal static class RunPythonTool
{
    [McpServerTool("run_python", "Run Python Script", false, true)]
    [Description("Execute a Python 3 script targeted at this slot's document. The script editor injects `__rhino_doc__` — use it as your document handle. Do NOT trust `scriptcontext.doc` or `rhinoscriptsyntax` calls.")]
    public static IToolResult RunPython(
        RhinoDoc doc,
        [Description("Script")] string script)
        => ScriptProjectRunner.RunScript(doc, Lang.Python3, script);
}
