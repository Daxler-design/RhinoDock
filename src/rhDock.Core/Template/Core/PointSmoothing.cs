#nullable enable

using System;
using System.Collections.Generic;
using Rhino.Geometry;

namespace MyProject.Core;

public static class PointSmoothing
{
    /// <summary>
    /// One Laplacian iteration: each point moves by <paramref name="strength"/> toward the midpoint of its two neighbors.
    /// A closed sequence wraps around; an open one keeps both end points. Locked points never move.
    /// </summary>
    public static List<Point3d> Laplacian(IReadOnlyList<Point3d> points, double strength, bool closed, IReadOnlyList<bool>? locked = null)
    {
        if (points is null)
            throw new ArgumentNullException(nameof(points));
        if (!double.IsFinite(strength))
            throw new ArgumentOutOfRangeException(nameof(strength), "Strength must be finite.");
        if (locked is not null && locked.Count != points.Count)
            throw new ArgumentException("Locked flags must match the point count.", nameof(locked));

        var next = new List<Point3d>(points);
        if (points.Count < 3)
            return next;

        int last = points.Count - 1;
        for (int i = 0; i <= last; i++)
        {
            if ((!closed && (i == 0 || i == last)) || (locked is not null && locked[i]))
                continue;
            Point3d previous = points[i == 0 ? last : i - 1];
            Point3d following = points[i == last ? 0 : i + 1];
            next[i] = points[i] + strength * ((previous + following) / 2 - points[i]);
        }
        return next;
    }

    /// <summary>Turning angle in radians at each point; open end points and degenerate points are 0.</summary>
    public static double[] TurnAngles(IReadOnlyList<Point3d> points, bool closed)
    {
        if (points is null)
            throw new ArgumentNullException(nameof(points));

        var angles = new double[points.Count];
        if (points.Count < 3)
            return angles;

        int last = points.Count - 1;
        for (int i = 0; i <= last; i++)
        {
            if (!closed && (i == 0 || i == last))
                continue;

            Point3d previous = points[i == 0 ? last : i - 1];
            Point3d following = points[i == last ? 0 : i + 1];
            var incoming = points[i] - previous;
            var outgoing = following - points[i];
            if (incoming.IsZero || outgoing.IsZero)
                continue;

            angles[i] = Vector3d.VectorAngle(incoming, outgoing);
        }

        return angles;
    }
}
