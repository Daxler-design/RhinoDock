# User guide

## Repositories and Scripts

A **Working Repo** is a C# project containing Scripts. A **Script** is a class with a public `Run(...)` method. RhinoDock runs one selected Script at a time.

In the Script tab, **New** creates a starter in an empty folder and **Browse** opens an existing repository. The picker searches names and category tags. To write your own, see [writing Scripts](scripts.md).

## Inputs

- Scalars (numbers, text, booleans, colors) are edited directly.
- A list input receives the whole list in one call; RhinoDock never runs your Script once per item.
- Geometry inputs pick Rhino objects and stay linked to them. Changing a picked object invalidates the old result.
- Sub-object inputs pick vertices, edges, or faces and carry their indices.

## Run, Preview, Bake

**Run** evaluates the Script once. **Auto** runs again when inputs or source change; it starts off each time you reopen a repository or switch Scripts. Compile, input, and execution errors appear in the panel, and a failed or outdated result is never shown or baked.

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
