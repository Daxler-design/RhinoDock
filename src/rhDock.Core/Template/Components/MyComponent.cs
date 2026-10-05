#nullable enable

using Rhino.Geometry;

namespace MyProject.Components;

public sealed class MyComponent
{
    public const string Name = "My Component";
    public const string Description = "Describe this script.";

    public void Run(Point3d input, ref Point3d Output)
    {
        Output = input;
    }
}
