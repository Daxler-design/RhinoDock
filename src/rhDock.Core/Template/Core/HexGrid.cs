#nullable enable

using System;
using System.Collections.Generic;
using Rhino.Geometry;

namespace MyProject.Core;

public static class HexGrid
{
    public static int Count(int rings) => rings < 0 ? 0 : 1 + 3 * rings * (rings + 1);

    public static List<(int Q, int R)> Coordinates(int rings)
    {
        if (rings < 0)
            throw new ArgumentOutOfRangeException(nameof(rings), "Rings must be nonnegative.");

        var coordinates = new List<(int Q, int R)>(Count(rings)) { (0, 0) };
        int[] steps = { 0, 1, -1, 1, -1, 0, 0, -1, 1, -1, 1, 0 };
        for (int ring = 1; ring <= rings; ring++)
        {
            int q = ring, r = -ring;
            for (int edge = 0; edge < 6; edge++)
            {
                int dq = steps[edge * 2], dr = steps[edge * 2 + 1];
                for (int step = 0; step < ring; step++)
                {
                    coordinates.Add((q, r));
                    q += dq;
                    r += dr;
                }
            }
        }

        return coordinates;
    }

    public static Point3d Center(Point3d origin, double sideLength, int q, int r)
    {
        double height = Math.Sqrt(3.0) * sideLength;
        return new Point3d(
            origin.X + 1.5 * sideLength * q,
            origin.Y + height * (r + q / 2.0),
            origin.Z);
    }

    public static Polyline Hexagon(Point3d center, double sideLength)
    {
        var hexagon = new Polyline();
        for (int corner = 0; corner <= 6; corner++)
        {
            double angle = corner * Math.PI / 3.0;
            hexagon.Add(new Point3d(
                center.X + sideLength * Math.Cos(angle),
                center.Y + sideLength * Math.Sin(angle),
                center.Z));
        }

        return hexagon;
    }
}
