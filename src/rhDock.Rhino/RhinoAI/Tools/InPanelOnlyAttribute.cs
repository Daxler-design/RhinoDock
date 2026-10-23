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

namespace rhDock.RhinoAI;

/// <summary>
/// Marks an MCP tool as available only to the in-Rhino AI panel agent (the
/// `/agent` endpoint). External callers on the `/` endpoint never see the tool
/// in tools/list and get a clear "panel-only" result if they invoke it anyway.
/// ask_user is panel-only: its answer is delivered as the panel agent's next
/// prompt, which an external client has no way to drive.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class InPanelOnlyAttribute : Attribute { }
