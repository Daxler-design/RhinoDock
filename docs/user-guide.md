# User guide

## Repositories and Scripts

A **Loaded Repository** is a C# project containing Scripts. A **Script** is a public entry point with a `Run(...)` method. RhinoDock runs one selected Script at a time; a **Workspace** is its saved configuration for that repository.

Use New to create a starter in an empty destination, or Browse to open an existing repository. New refuses a nonempty destination. The Script picker supports names and category tags. See [Writing Scripts](scripts.md) for the source contract.

Set scalar values directly; list inputs deliver the entire list in one call. Geometry inputs pick Rhino objects and stay linked to them. Sub-object inputs pick vertices, edges, or faces and carry their component indices. Changing picked geometry invalidates the old result.

## Run and inspect

Run evaluates the selected Script once. Auto coalesces changes and runs again when Rhino can process them. Compile, input, and execution errors appear in the panel; failed or outdated results cannot supply Preview or Bake.

Preview draws temporary geometry without adding document objects. Input and output style controls set color and applicable size or shading. Primitive outputs appear in bounded tables; `Info` output appears in the Console. Bake adds selected current outputs to the active Rhino document in one undo record. Preview visibility does not select what gets Baked.

Solver Scripts keep one instance across Frames. Solve plays Frames; Pause stops playback; Step runs one Frame; Reset discards the instance. Input changes and source reload also discard it. Keep work per Frame short so Rhino stays responsive.

## Save and resume

RhinoDock automatically writes `.rhdock/workspace.json` in the Loaded Repository. It keeps up to 16 revisions with per-Script inputs, picks, display settings, and latest Run Records. Reopening restores the latest configuration with Auto off. A restored Run Record is historical: Run again before Preview or Bake.

Picked object IDs must resolve in the active Rhino document. Missing objects need to be picked again. Moving a repository does not prevent opening it; moving or renaming an individual Script changes the key for its saved settings.

Export writes the latest Workspace revision to a separate JSON file. It is not an import mechanism. Save failures are warnings and do not prevent running Scripts. Do not edit runtime JSON to simulate a successful Run. Simultaneous Rhino processes do not merge writes to the same Workspace.

## Agent tab

Install and sign in to your chosen agent CLI using that agent's instructions, then select it in the Agent tab. The harness owns authentication, models, tools, and stored Conversations. RhinoDock provides context attachments, streamed replies, and supported permission, plan, review, and rewind controls.

Capabilities vary by agent. Pi/omp do not provide approval prompts. An agent's saved permissions may allow actions without a new card. Rewind and Undo are bounded conveniences, not a replacement for Git or backups. Open Conversations can continue working in the background.

Agent settings and MCP servers belong to the agent's own configuration. RhinoDock does not currently provide built-in Host Tools for agents to control Rhino. Read [security guidance](../SECURITY.md) and [known limitations](limitations.md).
