# Hex Attractor Scale

Scales each cell of a flat-top hexagonal layout by distance to optional attractors.

- **Layout**: axial grid from `Core/HexGrid`: flat-top hexagons with a vertex on +X, horizontal spacing `1.5 * sideLength`, vertical spacing `sqrt(3) * sideLength`. `rings` counts concentric rings; cell count is `1 + 3 * rings * (rings + 1)`.
- **Units**: `center`, `sideLength`, and `falloffRadius` are in document units. `minScale`/`maxScale` multiply `sideLength` (1 = the base hexagon), so a cell at scale `s` has side `sideLength * s`. `falloffPower` and scale factors are dimensionless.
- **Attractors**: optional; any mix of points and curves. With none, every cell has scale 1 and the output is the plain hex layout. Distance is the minimum Euclidean distance from the cell center to an attractor point or to the closest point on an attractor curve. Cell centers stay fixed on the base grid; only the hexagon size changes, so scaled cells may no longer share edges.
- **Falloff**: `t = clamp(1 - distance / falloffRadius, 0, 1)`, then `t = t^falloffPower`, and `scale = minScale + (maxScale - minScale) * t`. At an attractor (distance 0) the cell reaches `maxScale`; at `falloffRadius` or beyond it reaches `minScale`. `falloffPower = 1` is linear, `> 1` focuses growth near attractors, `< 1` spreads it outward.
- **Outputs**: `Cells` are the scaled hexagon polylines, `Centers` are the fixed grid centers, `Scales` are the applied factors in `Cells` order, and `Info` summarizes counts, parameters, and the actual scale range used.
