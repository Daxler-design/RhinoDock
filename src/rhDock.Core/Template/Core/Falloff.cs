#nullable enable

using System;
using System.Collections.Generic;
using Rhino.Geometry;

namespace MyProject.Core;

public static class Falloff
{
    public static double NearestDistance(
        Point3d location,
        IReadOnlyList<Point3d>? points,
        IReadOnlyList<Curve>? curves)
    {
        double best = double.PositiveInfinity;

        if (points is not null)
        {
            for (int i = 0; i < points.Count; i++)
                best = Math.Min(best, location.DistanceTo(points[i]));
        }

        if (curves is not null)
        {
            for (int i = 0; i < curves.Count; i++)
            {
                Curve? curve = curves[i];
                if (curve is null || !curve.IsValid)
                    continue;
                if (curve.ClosestPoint(location, out double t))
                    best = Math.Min(best, location.DistanceTo(curve.PointAt(t)));
            }
        }

        return best;
    }

    public static double Blend(double distance, double radius, double power, double minScale, double maxScale)
    {
        if (radius <= 0.0 || !double.IsFinite(distance))
            return maxScale;

        double t = 1.0 - distance / radius;
        if (t <= 0.0)
            return minScale;
        if (t >= 1.0)
            return maxScale;
        if (power != 1.0)
            t = Math.Pow(t, power);

        return minScale + (maxScale - minScale) * t;
    }
}
