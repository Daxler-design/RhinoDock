# Working repository guidance

One .NET 8 C# repository for Rhino 8 Scripts loaded by rhDock; it may contain several design projects. Keep version-controlled content in English. Project algorithms and assumptions belong in code and `Domain/<Project>/CONTEXT.md`.

## Structure and reuse

- Keep one `MyProject.csproj`. Add folders, projects, abstractions, or dependencies only for a current need; group related files and namespaces by project.
- `Components/`: small public `Run(...)` entry points with explicit host interaction. `Domain/`: project rules. `Core/`: reusable computations. Core must not depend on Domain or Components; Domain must not depend on Components.
- Inspect existing code and callers before adding an operation. Reuse only when geometry, units, tolerances, edge cases, and outputs fit; share common calculations without merging unrelated project rules.
- Keep code that changes together close together. Prefer static methods with plain parameters and tuple results; use classes for persistent state, such as Solver fields.
- Prefer host-independent data for new computations. Convert Rhino geometry near inputs/outputs; keep numerical topology, units, and tolerances explicit and library types inside the computation.
- Add Eigen, CGAL, libigl, Open CASCADE, or other libraries for a concrete benefit. Keep interop narrow and document conversions that affect precision or geometry. Test new DLL/native bridges on Windows, macOS, and source reload.
- Make document edits and side effects explicit. Keep durable project context short; exclude temporary task notes.

## Script contract

- rhDock compiles repository `.cs` files with Roslyn and reads DLL `<Reference>` entries with `HintPath` from `MyProject.csproj`. It does not run MSBuild, restore NuGet, or read the project's nullable setting. Use explicit `using` directives and `#nullable enable`.
- A Script is a public class in `Components/` with a public instance `Run(...)`. Optional `public const string` fields: `Name` (panel label), `Category` (tag/list ordering), and `Description`.
- Plain parameters are inputs; `ref`/`out` are outputs. Default values make inputs optional; put optional parameters last. Lists/arrays pass whole in one call, without host iteration.
- Panel inputs support `int`, `double`, `string`, `bool`, and their lists/arrays; `System.Drawing.Color` and `Rhino.Display.Color4f` inputs use a color picker and support single values or lists. Geometry inputs support `GeometryBase` and its public subclasses, plus `Point3d`; they pick linked Rhino objects and update when those objects change.
- `ComponentIndex` inputs pick Sub-object vertices, edges, or faces using Ctrl/Cmd+Shift-click or the prompt. They carry type/index only; obtain topology from separately picked geometry and validate indices.
- Geometry outputs support Preview/Bake; primitive outputs support tables. String output `Info` appears in the Console. Mesh vertex colors display when Preview color is Default.

## Execution

- Ordinary Run creates an instance and invokes `Run(...)` once. Auto reruns after input changes. Work inside a call blocks Rhino until it returns.
- `ref bool Done` declares a Solver Script: one instance across Frames, ending on Done, pause, error, or unchanged outputs (excluding Info). Controls provide Solve, Pause, Step, Reset, and frame rate; Auto restarts after input changes.
- Reset, input changes, reload, and Script switches discard the Solver instance. Static fields survive Reset within a compiled generation; rhDock does not reset them.

## Workspace and debugging

- Saving source in an open repository reloads Scripts and clears stale results; unsaved typing is not observed. Busy Rhino can delay compilation and saving. Read errors and Info in the Console.
- `.rhdock/workspace.json` stores selected Script, supported inputs/picks, display settings, and latest Run Records. Script keys are file paths; moving/renaming a Script starts without its saved settings. History keeps 16 complete revisions, not 16 Runs; unchanged Run Records can repeat.
- Panel `PID: <number>` identifies Rhino. Revision `pid` identifies the saver; `lastRun.pid` identifies the evaluator. Missing/null PID means unknown. Match PID with UTC because PIDs can be reused. Unsaved documents need no path.
- For debugging, search all revisions for the target Script and `lastRun.pid`; choose the newest matching `lastRun.utc`, not revision save time. Ask which panel PID if unclear. Hosts share the file without merging; final save controls restore, overlapping saves can lose revisions, and history can evict evidence.
- Read `lastRun.status`, `error`, `diagnostics`, `inputs`, `picks`, and `outputs` together. Compile details are in `error`; saved text is bounded. Check the live Console for more detail.
- Restored records are historical. Manual source reload does not prove execution: validate edited code with a fresh Run. Solvers record the latest Frame on stop/pause/completion, not every Frame. JSON does not prove live Preview/Bake or responsiveness; never edit it to simulate validation.

## Verification and build

- Before committing, run `dotnet build` and `git diff --check`; review scope/dependencies and report live Rhino checks separately. Builds prove compilation only.
- Never edit or commit `bin/`, `obj/`, or `.rhdock/`.
- Install Rhino 8 and .NET 8 SDK. Default Rhino assembly paths support Windows/macOS; override when needed:

```sh
dotnet build -p:RhinoAssemblyDir="/path/to/Rhino/assembly-directory"
```

## Presentation

When presenting reviews, audits, refactoring suggestions, or architectural analysis:

- Keep the response compact and visually structured.
- Start with a short summary of the main finding.
- Group findings by file, subsystem, or concern.
- Prefer short Markdown bullets over dense paragraphs or log-like output.
- Put file paths, symbols, types, and commands in `code formatting`.
- Use concise action labels such as **Remove**, **Simplify**, **Reuse**, **Keep**, and **Consider**.
- Separate high-confidence changes from optional suggestions.
- End with a brief impact summary when useful.
- Do not output raw streams of `file:line: action: explanation` unless explicitly requested.
- Do not sacrifice technical reasoning for presentation.
