using System;
using System.Linq;
using Rhino;
using rhDock.Extensions;

// A starter Extension: a web window that shows how many objects are selected in Rhino and logs a message from the page.
// Save this file and rhDock reloads it; the window stays open.
public sealed class MyExtension : IRhDockExtension, IDisposable
{
    private ExtensionView? view;

    public void Activate(ExtensionContext rh)
    {
        view = rh.OpenView("My Extension", "index.html");
        view.On("ready", _ => PostSelection());
        view.On("hello", _ => rh.Log("Hello from the page."));
        // Static Rhino events keep this generation alive until Dispose removes the handlers.
        RhinoDoc.SelectObjects += OnSelection;
        RhinoDoc.DeselectObjects += OnSelection;
        RhinoDoc.DeselectAllObjects += OnSelection;
    }

    public void Dispose()
    {
        RhinoDoc.SelectObjects -= OnSelection;
        RhinoDoc.DeselectObjects -= OnSelection;
        RhinoDoc.DeselectAllObjects -= OnSelection;
    }

    private void OnSelection(object? sender, EventArgs e) => PostSelection();

    private void PostSelection() => view?.Post("selection", RhinoDoc.ActiveDoc?.Objects.GetSelectedObjects(false, false).Count() ?? 0);
}
