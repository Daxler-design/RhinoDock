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

namespace rhDock.RhinoAI.Tools;

[McpServerToolType]
internal static class GetSelectionTool
{
    [McpServerTool("get_selection", "Get Selection", true, false)]
    [Description("Return all currently selected objects in global::Rhino.")]
    public static IToolResult GetSelection(RhinoDoc doc) =>
        Success(GetContextTool.SelectionOf(doc));
}
