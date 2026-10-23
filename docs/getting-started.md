# Getting started

## Install

Requirements: Rhino 8.31 or later, using .NET 8 (Windows or macOS).

In Rhino, run `_PackageManager`, search for `rhdock`, and install it. Or build the package from source (see the [README](https://github.com/Daxler-design/RhinoDock/blob/main/README.md#build-from-source)), install the `.yak` file with Rhino's Yak CLI or drag it onto Rhino, and restart Rhino. Install only one copy: if you loaded an `.rhp` directly, remove that registration first.

## First run

1. Run `RhinoDock` in Rhino to open the panel.
2. In the **Script** tab, choose **New** to create a starter repository in an empty folder, or **Browse** to open an existing one.
3. Select a Script, set its inputs, and pick any geometry it needs.
4. Choose **Run** (or **Solve** for a Solver). Check the outputs and the Preview in the viewport.
5. Choose **Bake** to add selected outputs to the document.

The starter has three Scripts: a minimal one, Hex Attractor Scale, and Laplacian Curve Smooth (a Solver). Edit any of them in your editor and save: RhinoDock recompiles and keeps compatible inputs.

## Where to go next

- Learn the panel: [user guide](user-guide.md).
- Write your own Script: [writing Scripts](scripts.md).
- Let an agent write it with you: [Agent tab](agent.md).
- Add your own windows or tools: [Extensions](extensions.md).

## Existing repositories

Updating the plugin does not replace your Working Repo or Extension Repo. Existing numeric and string mode inputs keep their current editor; declare a C# enum to opt into a dropdown. New Extension Repos include the ViewportSliders example; existing repos are not modified.
