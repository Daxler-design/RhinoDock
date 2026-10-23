using System;
using System.Collections.Generic;
using Rhino.Geometry;

public sealed class ViewportSliderScript
{
    public void Run(List<double> Values, List<Point3d> Anchors, out List<Line> Lines)
    {
        if (Values.Count != Anchors.Count) throw new ArgumentException("Supply one anchor per value.");
        Lines = new List<Line>();
        for (var i = 0; i < Values.Count; i++)
            Lines.Add(new Line(Anchors[i], Anchors[i] + Vector3d.ZAxis * Values[i]));
    }
}
