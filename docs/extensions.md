# Extensions

RhinoDock keeps its core small. An **Extension** adds what you need on top: a window, a way to enter values, a tie to the Rhino camera, a background process. Extensions are C# code that RhinoDock compiles and reloads in Rhino as you save, with no build or restart.

Scripts compute; Extensions handle interaction and integration. They are separate repositories and only data crosses between them.

## Extension Repo

An **Extension Repo** is a folder where each top-level folder with a `.csproj` is one Extension. Open the **Extensions** tab, then **New Extension Repo** to create one from the template, or **Open** to use an existing repo. The template contains:

- `AGENTS.md` and `CLAUDE.md`: the full contract, written so an agent can create Extensions from the repo alone.
- A starter `MyExtension` with a C# class and a web page.
- ViewportSliders and companion Script source showing inline editing through viewport handles.

Use **New Extension** to add another from the starter. Share an Extension by sharing its repo.

## Run an Extension

In the Extensions tab, switch an Extension on for this session, or check **Startup** to enable it whenever Rhino starts. Nothing loads unless you enable it. Save a `.cs`, `.csproj`, or `.dll` file and a new version compiles and replaces the running one. If it fails to compile or start, the old version keeps running and the error appears in the tab. Page files (`.html`, `.js`, `.css`) reload with the **Reload** button.

## What an Extension can do

An Extension is a public class implementing `IRhDockExtension` with `Activate(ExtensionContext rh)`. It can use everything Rhino has loaded (RhinoCommon, Eto, Grasshopper, .NET 8) plus its own references. The context adds only what an Extension cannot do alone:

- `OpenView(title, page)` opens a floating window with a web page. Windows survive reloads. Pages talk to C# through `window.rhdock`.
- `Scripts` is the panel's Script session: list, select, set inputs, run, read outputs, and register an editor for input types the panel cannot edit.
- `StartProcess(info)` starts a process that RhinoDock ends when the Extension is replaced or Rhino closes.
- `Log(message)` writes to the Rhino command line and the log.

Values cross between Extensions and Scripts as copies of .NET or RhinoCommon types. A Script never references an Extension.

## Trust

An Extension runs with your full trust inside Rhino, like a plug-in. It is not sandboxed, and a crash or a stuck UI thread affects Rhino. Read an Extension's source before you enable it.

## Share and find Extensions

Publish an Extension Repo on GitHub and add the `rhdock-extension` topic so others can find it. To use one, clone it and open it in the Extensions tab.

See also [debugging](debugging.md).

## ViewportSliders example

See [ViewportSliders](https://github.com/Daxler-design/RhinoDock/tree/main/examples/ViewportSliders) and its companion Working Repo. The Extension edits ordinary numeric list inputs using `SetInputItem`, synchronizes active rows through `SetActiveItem`, and explicitly reorders related inputs when needed. The host performs no implicit list matching. Its native callbacks and conduit are released on disposal.
