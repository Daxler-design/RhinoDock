# Extension Repo guidance

This repository holds rhDock Extensions for Rhino 8: C# code that rhDock compiles and reloads inside Rhino, without a build or a restart. Keep version-controlled content in English.

## Layout

- Each top-level folder with a `.csproj` is one Extension; the folder name is its name. Create further Extensions directly in folders, yourself or with an agent. A new repo starts with ViewportSliders; its README explains the companion Script setup.
- An Extension folder holds the `.csproj`, C# sources, and optionally web pages with `rhdock.js`. Shared code between Extensions is not supported; copy what you need.
- `Directory.Build.props` is written by rhDock for IDE completion and is not version-controlled.

## Lifecycle

- The user enables an Extension in the Extensions tab for the Rhino session. Extensions marked Load at startup are enabled when Rhino starts.
- Saving a `.cs` or `.csproj` file compiles a new generation and activates it. Only after activation succeeds is the old generation disposed and unloaded. A compile or activation error leaves the old generation running; the error shows in the Extensions tab and on the Rhino command line.
- Page files (`.html`, `.js`, `.css`) do not reload by themselves: use Reload in the Extensions tab, or point the view at a dev server with hot reload.
- Two seconds after a generation is retired, rhDock checks that it was collected and reports it as "still loaded" if not.

## Contract

- Exactly one public class implements `rhDock.Extensions.IRhDockExtension` with `void Activate(ExtensionContext rh)`. rhDock creates it once per generation. Implement `IDisposable` to undo what `Activate` registered.
- `ExtensionContext`: `Folder`, `Name`, `OpenView(title, page)`, `Scripts`, `StartProcess(info)`, `Log(message)`. `Log` writes to the Rhino command line and the rhDock log.
- `OpenView` opens a floating web window owned by rhDock, or reuses the window with the same title from the previous generation without reloading its page; a window the new generation does not open closes. `page` is a file in the Extension folder or an http(s) URL.
- `ExtensionView`: `On(type, handler)` receives `rhdock.send(type, payload)` from the page as a `JsonElement`; `Post(type, value)` serializes `value` to JSON (camelCase) and calls the page's `rhdock.on(type, …)` handler. Only the latest unsent value per type is kept, so post whole state, not deltas.
- Pages load `rhdock.js` first. Call `rhdock.ready()` after registering handlers; it sends `ready` until the first message arrives, so answer `ready` with the page's full state. Types starting with `rhdock.` are reserved: page errors and an environment report (Eto bridge, WebGPU, secure context) go to the rhDock log.
- Handlers run on the Rhino UI thread. Exceptions in `Activate`, handlers, and `Dispose` are caught and reported.
- `rh.Scripts` is the rhDock panel's Script Session; calls take the same path as the panel, so the panel and the Workspace follow. `List()` and `Selected` give Scripts with their ports (`Kind` is how the panel edits an input: number, int, text, boolean, list, color, pick, extension, unsupported). `Select(path)`, `SetInput(name, value)`, and `Run()` act like the panel. `Input(name)` and `Output(name)` return copies. `Status` and `Error` describe the last Run. `Changed` fires on the UI queue after any Session change; it needs no cleanup.
- `rh.Scripts.RegisterEditor(type, edit)` gives Script inputs of a .NET or RhinoCommon type that the panel cannot edit an Edit button; `edit(inputName)` typically opens a view and calls `SetInput`. Values persist when the Workspace can store them (scalars, colors, `DateTime`, `Guid`).
- Indexed editors use `RegisterEditor(type, (name, index) => ...)`: null index means a single value; list items are applied with `SetInputItem(name, index, value)`. Inline editors use `RegisterEditor(type, control, format, parse)`, with an existing `number`, `int`, `text`, `boolean`, or `color` control and invariant text. Parsed values must fit the registered type. Registrations end at retirement; parse errors are logged and keep the committed value.
- `ActiveItem` / `SetActiveItem(name, index)` synchronize active rows without selecting geometry. `ReorderInput(name, from, to)` preserves pick bindings and raises `Reordered` synchronously; explicitly update related inputs in that handler when your workflow requires it. rhDock never matches other inputs automatically. `HasDocument` and `Revision` let viewport tools reject stale interaction after document, Script, or source changes. Active rows are not saved.
- `rh.StartProcess(info)` starts a process that rhDock ends, with its children, when the generation is retired or Rhino closes.

## What an Extension may use

- Everything Rhino has loaded: RhinoCommon, Eto, Rhino.UI, Grasshopper, and .NET 8. Add other .NET 8 DLLs as `<Reference>` with a `HintPath` in the `.csproj`. rhDock does not run MSBuild or restore NuGet; use explicit `using` directives. Write C# 11, the version Rhino 8's compiler supports: no collection expressions (`[]`, `[a, b]`) or primary constructors on classes. The `.csproj` pins `LangVersion` 11 so `dotnet build` reports the same errors.
- An Extension runs with full trust inside Rhino, like a plug-in. A crash, deadlock, or long loop on the UI thread affects Rhino. Put heavy work on a background thread, or in another process through `StartProcess`; native DLLs loaded into Rhino can crash it and are never unloaded.

## Working with Scripts

- Only values of .NET or RhinoCommon types cross between Extensions and Scripts, and they are copied. A Script never references an Extension, and an Extension never loads a Script type. `SetInput` rejects anything else.
- Match Scripts by their ports, not by file path, so an Extension works with any Working Repo.
- Behavior a Script needs (an algorithm, a solver) belongs in the Working Repo, not in an Extension.

## Rules that keep reloads clean

- Undo in `Dispose` everything you registered outside the context: static Rhino events (`RhinoDoc.*`, `RhinoApp.Idle`, `DisplayPipeline.*`), display conduits (`Enabled = false`), timers, threads, Eto windows you created, and static caches. Anything left holds the old generation in memory; rhDock reports it as "still loaded".
- Use named methods for event handlers so `Dispose` can remove them; lambdas cannot be removed.
- Rhino raises some events many times per command (for example one per replaced object). Coalesce them, for example by setting a flag and doing the work once on `RhinoApp.Idle`.
- Do not write to the document inside display or document events. Wrap document edits in `doc.BeginUndoRecord` / `doc.EndUndoRecord` and call `doc.Views.Redraw()`.
- Keep Extension types out of objects that outlive the generation, such as `UserData` stored in the document or values handed to rhDock Scripts.

## Debugging

- Log: `rhdock-agent.log` in the system temporary folder (`$TMPDIR` on macOS) has compile times, activations, retirements, leak checks, window events, page errors, and the page environment.
- Web Inspector: on macOS, Safari → Develop → the Rhino entry → the window title. On Windows, right-click → Inspect.
- Browser: `RhinoDockMirror` opens a page that lists the open Extension windows. Each opens as a live copy in a browser tab, with that browser's developer tools; messages reach the same `ExtensionView` as the Rhino window, and `Post` reaches both. Pages need nothing extra: rhDock serves its own `rhdock.js` there.
- Breakpoints: start Rhino with `RHDOCK_DEBUG=1` so Extensions compile with embedded symbols, then attach a .NET debugger to Rhino.
- `dotnet build` in an Extension folder only type-checks; rhDock never uses its output. Never commit `bin/` or `obj/`.

## When a capability belongs in rhDock

Build new capabilities inside an Extension first. When several Extensions need the same thing, or it cannot be done safely from an Extension, describe it for rhDock with the Extensions that need it.
