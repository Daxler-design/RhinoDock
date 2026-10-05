# Curve Smoothing

Laplacian smoothing of arbitrary curves.

- **Units**: `strength` is dimensionless, greater than 0 and at most 1; `steps` is the iteration count; `samples` is a sample count per curve where `0` means the curve's own points; `sharpAngle` is in degrees.
- **Direction**: each iteration moves every point toward the midpoint of its two neighbors (`Core/PointSmoothing.Laplacian`). Open curves keep their end points; closed curves wrap around. Repeated smoothing straightens open curves and shrinks closed ones.
- **Sampling**: `samples = 0` uses polyline vertices, otherwise NURBS Greville points. `samples > 0` divides the curve into that many equal-length segments, approximating the input.
- **Lock Corners**: when `lockCorners` is true, points whose turning angle exceeds `sharpAngle` never move.
- **Iterative solve**: each Frame applies one iteration to every curve and sets `Done` after `steps` iterations. A new Solve starts again from the sampled input.
- **Outputs**: `Original` is the sampled input for comparison, `Polylines` are the current smoothed samples, and `SmoothCurves` are degree-3 interpolated curves through them (`CurveKnotStyle.Chord`, closed for closed inputs).
