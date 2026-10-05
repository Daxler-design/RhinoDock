#nullable enable

using System;
using System.Collections.Generic;
using MyProject.Core;
using Rhino.Geometry;

namespace MyProject.Domain.HexAttractor;

public static class HexAttractorDomain
{
    public static (List<Polyline> Cells, List<Point3d> Centers, List<double> Scales) Build(
        Point3d center,
        double sideLength,
        int rings,
        IReadOnlyList<Point3d>? attractorPoints,
        IReadOnlyList<Curve>? attractorCurves,
        double falloffRadius,
        double falloffPower,
        double minScale,
        double maxScale)
    {
        attractorPoints ??= Array.Empty<Point3d>();
        attractorCurves ??= Array.Empty<Curve>();
        if (!center.IsValid)
            throw new ArgumentException("Provide a valid center point.", nameof(center));
        if (!double.IsFinite(sideLength) || sideLength <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(sideLength), "Side length must be positive and finite.");
        if (rings < 0)
            throw new ArgumentOutOfRangeException(nameof(rings), "Rings must be nonnegative.");
        if (!double.IsFinite(falloffRadius) || falloffRadius <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(falloffRadius), "Falloff radius must be positive and finite.");
        if (!double.IsFinite(falloffPower) || falloffPower <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(falloffPower), "Falloff power must be positive and finite.");
        if (!double.IsFinite(minScale) || minScale <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(minScale), "Minimum scale must be positive and finite.");
        if (!double.IsFinite(maxScale) || maxScale < minScale)
            throw new ArgumentOutOfRangeException(nameof(maxScale), "Maximum scale must be finite and at least the minimum scale.");

        bool attracted = attractorPoints.Count > 0 || attractorCurves.Count > 0;
        List<(int Q, int R)> coordinates = HexGrid.Coordinates(rings);
        var cells = new List<Polyline>(coordinates.Count);
        var centers = new List<Point3d>(coordinates.Count);
        var scales = new List<double>(coordinates.Count);
        foreach ((int q, int r) in coordinates)
        {
            Point3d cellCenter = HexGrid.Center(center, sideLength, q, r);
            double scale = attracted
                ? Falloff.Blend(Falloff.NearestDistance(cellCenter, attractorPoints, attractorCurves), falloffRadius, falloffPower, minScale, maxScale)
                : 1.0;
            centers.Add(cellCenter);
            scales.Add(scale);
            cells.Add(HexGrid.Hexagon(cellCenter, sideLength * scale));
        }
        return (cells, centers, scales);
    }
}
