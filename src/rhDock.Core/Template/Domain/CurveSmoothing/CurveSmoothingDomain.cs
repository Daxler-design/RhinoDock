#nullable enable

using System;
using System.Collections.Generic;
using MyProject.Core;
using Rhino;
using Rhino.Geometry;

namespace MyProject.Domain.CurveSmoothing;

public static class CurveSmoothingDomain
{
    /// <summary>Smoothing state of one curve, kept across Frames.</summary>
    public sealed record CurveState(List<Point3d> Original, bool Closed, bool[]? Locked)
    {
        public List<Point3d> Current { get; set; } = Original;
    }

    /// <summary>Validates the settings and samples every curve. With lockCorners, points turning more than sharpAngleDegrees are locked.</summary>
    public static (List<CurveState> Curves, int LockedCorners) Start(
        IReadOnlyList<Curve> curves, int steps, double strength, int samples, bool lockCorners, double sharpAngleDegrees)
    {
        if (curves is null || curves.Count == 0)
            throw new ArgumentException("Provide at least one curve.", nameof(curves));
        if (steps < 0)
            throw new ArgumentOutOfRangeException(nameof(steps), "Steps must be nonnegative.");
        if (!double.IsFinite(strength) || strength <= 0.0 || strength > 1.0)
            throw new ArgumentOutOfRangeException(nameof(strength), "Strength must be greater than 0 and at most 1.");
        if (samples < 0)
            throw new ArgumentOutOfRangeException(nameof(samples), "Samples must be 0 (full) or positive.");
        if (lockCorners && (sharpAngleDegrees <= 0.0 || sharpAngleDegrees >= 180.0))
            throw new ArgumentOutOfRangeException(nameof(sharpAngleDegrees), "Sharp angle must be between 0 and 180 degrees.");

        double threshold = RhinoMath.ToRadians(sharpAngleDegrees);
        var states = new List<CurveState>(curves.Count);
        int lockedCorners = 0;
        for (int i = 0; i < curves.Count; i++)
        {
            Curve curve = curves[i];
            if (curve is null || !curve.IsValid)
                throw new ArgumentException($"Curve {i} is not valid.", nameof(curves));

            bool closed = curve.IsClosed;
            List<Point3d> points = SamplePoints(curve, samples, closed);
            bool[]? locked = null;
            if (lockCorners)
            {
                double[] turns = PointSmoothing.TurnAngles(points, closed);
                locked = new bool[points.Count];
                for (int p = 0; p < turns.Length; p++)
                {
                    locked[p] = turns[p] > threshold;
                    if (locked[p]) lockedCorners++;
                }
            }
            states.Add(new CurveState(points, closed, locked));
        }
        return (states, lockedCorners);
    }

    /// <summary>Applies one Laplacian iteration to every curve.</summary>
    public static void Step(List<CurveState> curves, double strength)
    {
        foreach (CurveState curve in curves)
            curve.Current = PointSmoothing.Laplacian(curve.Current, strength, curve.Closed, curve.Locked);
    }

    public static Polyline ToPolyline(List<Point3d> points, bool closed)
    {
        var polyline = new Polyline(points);
        if (closed && points.Count > 2)
            polyline.Add(points[0]);
        return polyline;
    }

    public static Curve ToSmoothCurve(List<Point3d> points, bool closed)
    {
        if (!closed || points.Count < 3)
            return Curve.CreateInterpolatedCurve(points, 3, CurveKnotStyle.Chord);

        var loop = new List<Point3d>(points) { points[0] };
        return Curve.CreateInterpolatedCurve(loop, 3, CurveKnotStyle.Chord);
    }

    private static List<Point3d> SamplePoints(Curve curve, int samples, bool closed)
    {
        List<Point3d> points;
        if (samples == 0)
        {
            points = FullPoints(curve);
        }
        else
        {
            curve.DivideByCount(samples, true, out Point3d[] divided);
            if (divided is null || divided.Length < 2)
                throw new ArgumentException("A curve could not be divided into the requested number of samples.", nameof(curve));
            points = new List<Point3d>(divided);
        }

        if (closed && points.Count > 1 && points[0].DistanceTo(points[points.Count - 1]) <= RhinoMath.ZeroTolerance)
            points.RemoveAt(points.Count - 1);
        return points;
    }

    private static List<Point3d> FullPoints(Curve curve)
    {
        if (curve.TryGetPolyline(out Polyline polyline))
            return new List<Point3d>(polyline);

        NurbsCurve? nurbs = curve.ToNurbsCurve();
        if (nurbs is null)
            throw new ArgumentException("A curve could not be converted to full sample points.", nameof(curve));
        return new List<Point3d>(nurbs.GrevillePoints());
    }
}
