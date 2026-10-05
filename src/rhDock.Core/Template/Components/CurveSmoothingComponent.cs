#nullable enable

using System.Collections.Generic;
using System.Globalization;
using MyProject.Domain.CurveSmoothing;
using Rhino.Geometry;

namespace MyProject.Components;

public sealed class CurveSmoothingComponent
{
    public const string Name = "Laplacian Curve Smooth";
    public const string Category = "Curve";
    public const string Description = "Smooths curves with one Laplacian iteration per Frame until Steps is reached. Lock Corners keeps sharp turns fixed.";

    private List<CurveSmoothingDomain.CurveState>? state;
    private List<Polyline>? original;
    private int lockedCorners;
    private int iteration;

    public void Run(
        List<Curve> curves,
        ref List<Polyline> Original,
        ref List<Polyline> Polylines,
        ref List<Curve> SmoothCurves,
        ref string Info,
        ref bool Done,
        int steps = 50,
        double strength = 0.5,
        int samples = 0,
        bool lockCorners = false,
        double sharpAngle = 30.0)
    {
        if (state is null)
        {
            (state, lockedCorners) = CurveSmoothingDomain.Start(curves, steps, strength, samples, lockCorners, sharpAngle);
            original = state.ConvertAll(c => CurveSmoothingDomain.ToPolyline(c.Original, c.Closed));
        }

        if (iteration < steps)
        {
            CurveSmoothingDomain.Step(state, strength);
            iteration++;
        }
        Original = new List<Polyline>(original!);
        Polylines = state.ConvertAll(c => CurveSmoothingDomain.ToPolyline(c.Current, c.Closed));
        SmoothCurves = state.ConvertAll(c => CurveSmoothingDomain.ToSmoothCurve(c.Current, c.Closed));
        Done = iteration >= steps;
        Info = string.Create(
            CultureInfo.InvariantCulture,
            $"Iteration {iteration}/{steps} on {state.Count} curve(s){(lockCorners ? $"; {lockedCorners} corner(s) locked above {sharpAngle:0.#} deg" : string.Empty)}.");
    }
}
