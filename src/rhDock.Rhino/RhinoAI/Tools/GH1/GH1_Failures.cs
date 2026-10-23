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

internal static class GH1_Failures
{

    public static IToolResult NoDocument => Failure(ToolError.GH_Document_NotFound, guidance: "Start Grasshopper with g1_start, or ask the user for assistance");

}
