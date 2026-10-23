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
/// All MCP Toolsare forced on the UI Thread, this attribute excludes a tool from that
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class BackgroundThreadAttribute : Attribute { }
