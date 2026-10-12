# Architecture

```text
Working Repo -> Roslyn / ScriptRunner -> SingleScriptSession
                                                    |
                                              Rhino host
                                         inputs / Preview / Bake
                                                    |
                                           Eto WebView panel
```

## Modules

- `rhDock.Core` compiles and discovers Scripts, runs the selected entry, manages collectible generations, and serializes supported Workspace values. It has no compile-time dependency on RhinoCommon, Eto, or Grasshopper. The starter repository is embedded here.
- `rhDock.Rhino` owns commands, document binding, picks, UI-thread dispatch, Solver pacing, Preview, Bake, and the Eto WebView. `SingleScriptHost` adapts the Core Session; `SingleScriptPanel` exposes explicit actions and bounded JSON snapshots.
- `rhDock.Agents` owns agent process lifetime, protocol adapters, capability metadata, and bounded text checkpoints. It is independent of Rhino and Script state. `AgentHost` in the Rhino module retains one harness per open Conversation and dispatches updates to the panel.
- Extensions (in `rhDock.Rhino`) are folders in a separate Extension Repo. Each compiles into its own collectible load context and activates before the previous generation is disposed and unloaded. The public contract is `rhDock.Extensions`: `IRhDockExtension` and a small `ExtensionContext` (`OpenView`, `Scripts`, `StartProcess`, `Log`). Every member is a compatibility promise, so new capabilities start inside an Extension. Only copies of .NET or RhinoCommon values cross between Extensions and Scripts.
- `frontend` renders snapshots with React. It sends actions through the Eto bridge and does not own geometry or execution state. Production assets are embedded in the plugin as one offline HTML file.

## Execution and lifetime

One ordinary Run invokes the selected Script at most once and uses a fresh instance. Lists remain whole inputs. A Solver holds one instance for its Solve and releases it on Reset, input change, failure, reload, or selection change.

Script-owned types, instances, assemblies, outputs, delegates, and reflection objects must not survive their generation. Clear host references before releasing the collectible load context. Dirty, failed, or retired outputs cannot remain available for Preview or Bake.

Rhino document and UI operations run through the Rhino host. Picks retain document IDs and extract geometry for execution; persistent state does not contain live document objects. Each Rhino process has its own host and Session.

## Persistence

`SingleScriptWorkspaceStore` writes version 3 `.rhdock/workspace.json` atomically, retaining at most 16 revisions. Revisions remember visited Scripts by portable file path. Saved Run Records contain supported values and summaries, not executable objects or output geometry.

Restored records are historical. Auto starts off after reopening or switching Scripts. Invalid or unsupported files are preserved before replacement; I/O failures remain warnings. Concurrent processes do not merge Workspace writes.

## UI and agents

The WebView sends narrowly named actions and receives plain JSON. Revision identifiers reject stale actions. `SnapshotSender` keeps one in-flight send and the latest pending snapshot; it completes on the UI thread without building an unbounded queue.

Agent protocols and credentials stay in their adapters and native agent configuration. Conversations retain independent processes and background activity. Agent callbacks update state under a lock, then dispatch UI refresh outside that lock. Never block the UI thread while holding a conversation lock.

The optional Mirror (`RhinoDockMirror`) serves the panel and Extension windows to a browser on a loopback port behind a per-start token, and refuses other origins. The installed frontend is self-contained; using the Mirror is an explicit action.
