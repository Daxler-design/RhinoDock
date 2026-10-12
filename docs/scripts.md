# Writing Scripts

Start with New in the panel. The generated repository includes its own `AGENTS.md` with detailed authoring guidance, a .NET 8 project, and example Scripts.

## Entry point

Use a public class with a public instance `Run(...)` method. Plain parameters are inputs; `ref` and `out` parameters are outputs. Default values make inputs optional. Optional `public const string` fields `Name`, `Category`, and `Description` provide panel metadata.

```csharp
#nullable enable
using Rhino.Geometry;

namespace MyProject.Components;

public sealed class CircleScript
{
    public const string Name = "Circle";
    public const string Category = "Examples";

    public void Run(Point3d center, ref Curve Output, double radius = 5.0)
    {
        Output = new Circle(center, radius).ToNurbsCurve();
    }
}
```

Put this in the generated project's `Components/` directory. Pick a Rhino point object for `center`, then Run. Arrays and lists pass as whole values; write iteration explicitly in C#.

Supported panel inputs include primitive scalars and lists, colors, Rhino geometry, `Point3d`, and `ComponentIndex`. A component index identifies a part, not its parent geometry; take a separate geometry input and validate the index when topology is needed. Geometry outputs support Preview/Bake. A string output named `Info` provides concise Console information.

## Solver Scripts

Declare `ref bool Done` (or `out bool Done`) for a Solver. The host keeps the instance across Frames, so fields can hold state. Each Frame calls `Run(...)` once. Set Done to finish. The host also stops playback on error or repeated unchanged outputs. Done is hidden from output controls.

Use the packaged [CurveSmoothingComponent](https://github.com/Daxler-design/RhinoDock/blob/main/src/rhDock.Core/Template/Components/CurveSmoothingComponent.cs) as an example. Reset, input changes, source reload, and Script switches discard the instance. Static fields survive Reset within a compiled generation; do not use them for Solve state. Long calls block Rhino, so divide expensive work across Frames.

## Compilation and dependencies

RhinoDock uses Roslyn to compile repository C# files. It does not run MSBuild or restore a Script project's NuGet packages. Use explicit `using` directives and `#nullable enable` where required.

External assemblies use `<Reference>` entries with `HintPath` in the project file; place distributable dependencies in `lib/`. Resolve dependencies before opening the repository and test native libraries on each supported operating system. The generated project documents how to override its Rhino assembly path for a normal `dotnet build`.

Saving source triggers reload, preserves compatible inputs, and clears stale results. Read compiler messages in the Console and validate with a fresh Run. Scripts run with Rhino's process permissions; file and document side effects are the Script author's responsibility.
