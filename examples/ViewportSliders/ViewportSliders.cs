using System;
using System.Drawing;
using Point = System.Drawing.Point;
using System.Linq;
using Rhino;
using Rhino.Commands;
using Rhino.Display;
using Rhino.Geometry;
using Rhino.UI;
using rhDock.Extensions;

/// <summary>Explicitly pairs the Values input with the Anchors input; the host performs no matching.</summary>
public sealed class ViewportSliders : IRhDockExtension, IDisposable
{
    private ExtensionScripts scripts = null!;
    private SliderDisplay display = null!;
    private SliderMouse mouse = null!;
    private double[] values = Array.Empty<double>();
    private Point3d[] anchors = Array.Empty<Point3d>();
    private int revision = -1;
    private uint document;
    private const int Width = 120;
    private const double Minimum = 0, Maximum = 10;

    public void Activate(ExtensionContext rh)
    {
        scripts = rh.Scripts;
        display = new SliderDisplay(this);
        mouse = new SliderMouse(this);
        scripts.Changed += Sync;
        scripts.Reordered += Reordered;
        RhinoApp.EscapeKeyPressed += Escape;
        Sync();
    }

    private bool Eligible => scripts.HasDocument && !Command.InCommand() && scripts.Revision == revision && values.Length > 0;

    private void Escape(object? sender, EventArgs e) => mouse.Stop();

    private void Sync()
    {
        var nextDocument = RhinoDoc.ActiveDoc?.RuntimeSerialNumber ?? 0;
        if (revision != scripts.Revision || document != nextDocument || !scripts.HasDocument) mouse.Stop();
        revision = scripts.Revision;
        document = nextDocument;
        var ports = scripts.Selected?.Ports;
        var matched = ports?.Any(p => p.Name == "Values" && !p.IsOutput && p.Kind == "list") == true
            && ports.Any(p => p.Name == "Anchors" && !p.IsOutput && p.Kind == "pick");
        var nextValues = matched ? scripts.Input("Values") as double[] : null;
        var nextAnchors = matched ? scripts.Input("Anchors") as Point3d[] : null;
        if (nextValues is null || nextAnchors is null || nextValues.Length != nextAnchors.Length
            || nextValues.Any(v => !double.IsFinite(v)) || nextAnchors.Any(p => !p.IsValid))
        {
            nextValues = Array.Empty<double>();
            nextAnchors = Array.Empty<Point3d>();
        }
        if (values.Length != nextValues.Length) mouse.Stop();
        values = nextValues;
        anchors = nextAnchors;
        var enabled = Eligible;
        if (display.Enabled != enabled) display.Enabled = enabled;
        if (mouse.Enabled != enabled) mouse.Enabled = enabled;
        RhinoDoc.ActiveDoc?.Views.Redraw();
    }

    private void Reordered(ScriptInputReorder move)
    {
        mouse.Stop();
        // This example chooses a positional pair and explicitly moves its anchor through the public API.
        if (move.Name == "Values" && scripts.Input("Values") is double[] numbers
            && scripts.Input("Anchors") is Point3d[] points && numbers.Length == points.Length)
            scripts.ReorderInput("Anchors", move.From, move.To);
    }

    private Rectangle Bounds(RhinoViewport viewport, int index)
    {
        if (!viewport.IsVisible(anchors[index])) return Rectangle.Empty;
        var point = viewport.WorldToClient(anchors[index]);
        return new Rectangle((int)point.X - Width / 2, (int)point.Y + 12, Width, 24);
    }

    private void SetValue(RhinoView view, Point point, int index)
    {
        if (!Eligible || view.Document.RuntimeSerialNumber != document || index >= values.Length
            || scripts.Input("Values") is not double[] current || index >= current.Length) { mouse.Stop(); return; }
        var rect = Bounds(view.ActiveViewport, index);
        if (rect.IsEmpty) { mouse.Stop(); return; }
        var fraction = Math.Clamp((point.X - rect.Left) / (double)Width, 0, 1);
        var value = Math.Round(Minimum + fraction * (Maximum - Minimum), 2);
        scripts.SetInputItem("Values", index, value);
        values[index] = value;
        view.Redraw();
    }

    private sealed class SliderDisplay : DisplayConduit
    {
        private readonly ViewportSliders owner;
        public SliderDisplay(ViewportSliders owner) => this.owner = owner;

        protected override void DrawForeground(DrawEventArgs e)
        {
            if (!owner.Eligible || e.RhinoDoc.RuntimeSerialNumber != owner.document) return;
            for (var i = 0; i < owner.values.Length; i++)
            {
                var bounds = owner.Bounds(e.Viewport, i);
                if (bounds.IsEmpty) continue;
                var active = owner.scripts.ActiveItem is { Name: "Values", Index: var index } && index == i;
                var color = active ? Color.Gold : Color.LightSteelBlue;
                e.Display.Draw2dRectangle(bounds, color, active ? 2 : 1, Color.FromArgb(230, 30, 30, 30));
                var fraction = Math.Clamp((owner.values[i] - Minimum) / (Maximum - Minimum), 0, 1);
                var x = bounds.Left + (int)(fraction * Width);
                e.Display.Draw2dLine(new Point(x, bounds.Top + 2), new Point(x, bounds.Bottom - 2), color, 3);
                e.Display.Draw2dText($"{i}: {owner.values[i]:0.##}", Color.White, new Point2d(bounds.Left + 5, bounds.Top + 4), false, 12);
            }
        }
    }

    private sealed class SliderMouse : MouseCallback
    {
        private readonly ViewportSliders owner;
        private int dragging = -1;
        private uint view;
        public SliderMouse(ViewportSliders owner) => this.owner = owner;
        public void Stop() { dragging = -1; view = 0; }

        protected override void OnMouseLeave(MouseCallbackEventArgs e)
        {
            if (e.View.RuntimeSerialNumber == view) Stop();
        }

        protected override void OnMouseDown(MouseCallbackEventArgs e)
        {
            owner.Sync();
            if (!owner.Eligible || e.MouseButton != MouseButton.Left || e.ShiftKeyDown || e.CtrlKeyDown
                || e.IsOverGumball() != Rhino.UI.Gumball.GumballMode.None || e.View.Document.RuntimeSerialNumber != owner.document) return;
            for (var i = owner.values.Length - 1; i >= 0; i--)
            {
                if (!owner.Bounds(e.View.ActiveViewport, i).Contains(e.ViewportPoint)) continue;
                dragging = i;
                view = e.View.RuntimeSerialNumber;
                e.Cancel = true;
                owner.scripts.SetActiveItem("Values", i);
                owner.SetValue(e.View, e.ViewportPoint, i);
                return;
            }
        }

        protected override void OnMouseMove(MouseCallbackEventArgs e)
        {
            if (dragging < 0) return;
            if (!owner.Eligible || e.View.RuntimeSerialNumber != view) { Stop(); return; }
            e.Cancel = true;
            owner.SetValue(e.View, e.ViewportPoint, dragging);
        }

        protected override void OnMouseUp(MouseCallbackEventArgs e)
        {
            if (dragging < 0 || e.MouseButton != MouseButton.Left) return;
            e.Cancel = true;
            if (e.View.RuntimeSerialNumber == view) owner.SetValue(e.View, e.ViewportPoint, dragging);
            Stop();
        }
    }

    public void Dispose()
    {
        if (scripts is not null) { scripts.Changed -= Sync; scripts.Reordered -= Reordered; }
        RhinoApp.EscapeKeyPressed -= Escape;
        if (mouse is not null) { mouse.Stop(); mouse.Enabled = false; }
        if (display is not null) display.Enabled = false;
        RhinoDoc.ActiveDoc?.Views.Redraw();
    }
}
