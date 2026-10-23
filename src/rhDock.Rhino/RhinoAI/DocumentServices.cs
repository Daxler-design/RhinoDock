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

internal sealed class DocumentServices(RhinoDoc doc) : IServiceProvider
{
    private RhinoDoc Doc { get; } = doc;

    public object? GetService(Type serviceType) =>
        serviceType == typeof(RhinoDoc) ? Doc : null;
}
