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

#if R9
#endif

namespace rhDock.RhinoAI.Tools;

internal static partial class GetContextTool
{
    public sealed record GrasshopperSummary(string Version, bool CanvasOpen, int ComponentCount, int WireCount);
}
