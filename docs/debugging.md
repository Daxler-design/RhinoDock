# Debugging and the Mirror

## Log

Plug-in, panel, agent, and Extension events go to one file, `rhdock-agent.log`, in the system temporary folder (`$TMPDIR` on macOS). It records compile times, activations, window events, page errors, and any unhandled exception before Rhino exits. Include it when you report a problem, after removing private content.

## Mirror

Run `RhinoDockMirror` to open the Mirror in your default browser; run it again to stop it. The page lists the RhinoDock panel and any open Extension windows. Each opens as a live copy in a browser tab with that browser's developer tools:

- The panel copy shows Rhino's current state, and its actions act in Rhino.
- An Extension window copy receives the same messages as the Rhino window and sends its controls' messages to the Extension. Saving the Extension reloads the copy.

The Mirror listens on `localhost` only, behind a random token that changes on each start. Windows that show an `http(s)` page are listed but not mirrored, and agent images and native dialogs stay in Rhino.

## Web inspector

In Rhino on macOS, use Safari → Develop → your Rhino entry → the window title. On Windows, right-click a panel or Extension window and choose Inspect.

## Breakpoints

Start Rhino with `RHDOCK_DEBUG=1` so Scripts and Extensions compile unoptimized with embedded symbols and sources. Then attach a .NET debugger to Rhino. `dotnet build` in an Extension folder only type-checks; RhinoDock never uses its output.
