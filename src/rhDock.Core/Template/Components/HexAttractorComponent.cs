#nullable enable

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MyProject.Domain.HexAttractor;
using Rhino.Geometry;

namespace MyProject.Components;

public sealed class HexAttractorComponent
{
    public const string Name = "Hex Attractor Scale";
    public const string Category = "Pattern";
    public const string Description = "Lays out hexagons and scales each cell by its distance to optional attractor points and curves.";

    public void Run(
        Point3d center,
        ref List<Polyline> Cells,
        ref List<Point3d> Centers,
        ref List<double> Scales,
        ref string Info,
        double sideLength = 1.0,
        int rings = 4,
        List<Point3d>? attractorPoints = null,
        List<Curve>? attractorCurves = null,
        double falloffRadius = 5.0,
        double falloffPower = 1.0,
        double minScale = 0.2,
        double maxScale = 1.0)
    {
        (Cells, Centers, Scales) = HexAttractorDomain.Build(
            center, sideLength, rings, attractorPoints, attractorCurves, falloffRadius, falloffPower, minScale, maxScale);
        int points = attractorPoints?.Count ?? 0, curves = attractorCurves?.Count ?? 0;
        Info = points + curves == 0
            ? $"Laid out {Cells.Count} hexagons at base size; pick attractor points or curves to scale them."
            : string.Create(
                CultureInfo.InvariantCulture,
                $"Scaled {Cells.Count} hexagons using {points} point(s) and {curves} curve(s); radius={falloffRadius:0.###}, power={falloffPower:0.###}, scale={minScale:0.###}..{maxScale:0.###} (used {Scales.Min():0.###}..{Scales.Max():0.###}).");
    }
}
