# User guide

## Repositories and Scripts

A **Working Repo** is a C# project containing Scripts. A **Script** is a class with a public `Run(...)` method. RhinoDock runs one selected Script at a time.

In the Script tab, **New** creates a starter in an empty folder and **Browse** opens an existing repository. The picker searches names and category tags. To write your own, see [writing Scripts](scripts.md).

## Inputs

- Scalars (numbers, text, booleans, colors) are edited directly. C# enum inputs show a dropdown with named choices and the declared default.
- Primitive list details support per-item edits and ordering. Lists remain whole Script inputs.
- A list input receives the whole list in one call; RhinoDock never runs your Script once per item.
- Geometry inputs pick Rhino objects and stay linked to them. Changing a picked object invalidates the old result.
- Sub-object inputs pick vertices, edges, or faces and carry their indices.

## Run, Preview, Bake

**Run** evaluates the Script once. **Auto** runs again when inputs or source change, pacing requests from measured Run duration; it starts off each time you reopen a repository or switch Scripts. Compile, input, and execution errors appear in the panel, and a failed or outdated result is never shown or baked.

**Preview** draws temporary geometry without touching the document, with color and size controls. Primitive outputs appear in tables, and an `Info` string output appears in the Console. **Bake** adds the outputs you select to the active document as one undo step. What you preview and what you bake are chosen separately.

## Solvers

A Script that declares a `Done` output is a Solver. **Solve** plays frames, **Pause** stops, **Step** runs one frame, and **Reset** discards its state. Changing an input or reloading the source also resets it. Keep each frame short so Rhino stays responsive.

## Save and resume

RhinoDock saves your inputs, picks, and display settings per Script in `.rhdock/workspace.json` inside the repository, keeping the last 16 versions. When you reopen, the settings come back with Auto off. The previous result is shown only as history: Run again before you Preview or Bake.

Picked objects must still exist in the active Rhino document, otherwise pick them again. Moving or renaming a Script file changes the key for its saved settings. **Export** writes the latest Workspace to a separate JSON file; it is not an import. Two Rhino processes on the same repository do not merge saves.

## More tabs

- [Agent](agent.md): a coding agent that works on your Scripts.
- [Extensions](extensions.md): your own windows and Rhino integration.
- [Debugging and the Mirror](debugging.md): logs and a browser copy of the panel.

## Panel organization and input history

Use Arrange to reorder inputs and outputs, create named groups, and fold sections. Group eyes and per-port eyes retain their state when a section's master Preview switch is off. The Inputs master switch controls only inputs; the Outputs switch controls only outputs.

With focus in the Script panel outside a text editor, Ctrl/Cmd+Z undoes supported committed scalar or primitive-list edits. Ctrl+Y or Ctrl/Cmd+Shift+Z redoes them. A completed slider scrub is one edit. Text fields retain their native text-edit history. Script-input history is separate from Rhino document history, resets with source/Script/document changes, and does not include geometry picks, styles, ranges, or layout.

## Fill and edges

The existing Preview Style picker has overlapping circular selectors: solid for fill, ring for edges. Each channel has its own color and opacity. Restore the fill's default to use the mesh's own analysis colors; overriding edges does not override fill. Width zero hides edges and opacity zero hides the selected channel. Preview style does not change the Script's output or the target layer attributes used by Bake.

## Appearance

Open Appearance from the repository menu to choose Light or Dark and an accent color. Color pickers share recent colors and offer hex and HSV input.
