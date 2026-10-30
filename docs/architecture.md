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
- `rhDock.Cli` is the bundled `rhdock` command-line bridge to an authenticated, revocable host connection. It contains no agent loop.
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

## Preview channels and panel history

PreviewStyle retains legacy color/shading fields and adds optional Edges and Fill channels. Each channel has an optional RGB override and opacity. PreviewItem prepares host-owned geometry, materials, and face/edge caches once per rebuild. Automatic colored meshes use the indexed shaded overload at partial opacity because the ordinary overload suppresses vertex colors. Explicit fill overrides clear colors only on the preview copy.

AgentHost publishes a history cursor and an initial suffix of 100 items. A valid earlier-page request extends the suffix; stale cursors are rejected. WindowedTranscript uses existing MessageScroller visibility, measured placeholders, and retained disclosure state. This is panel publication/rendering control; the harness replay remains in host memory and no second persistent transcript store is introduced.

## Host tools

HostConnection owns one revocable loopback capability per enabled Rhino instance, private credentials and retry outcomes. AgentHost retains the switch independently of conversation disposal. HostTools binds the active document per request and rechecks opaque targets on the UI thread after queueing. Document/repository changes invalidate targets without disconnecting. The CLI discovers enabled instances, verifies PID start times, and supports automatic or explicit target binding; optional source labels route conversation tool cards. Harness permission modes are not a second bridge authorization policy. HostTools combines Script-session operations with the vendored RhinoAI tool registry. Both reach existing Rhino host operations; Core remains independent of Rhino. The harness owns the agent loop, model context, and tools outside this bridge. See the vendored RhinoAI UPSTREAM.md and THIRD-PARTY-NOTICES.md for provenance.

On Windows, agent subprocesses use CreateProcessW with an explicit inherited-handle list containing only stdin/stdout/stderr pipes. Existing Rhino handle flags are unchanged. AgentChild owns the redirected streams and process; other platforms retain the ordinary Process.Start path. This protects the shared agent launcher and Git metadata queries, not arbitrary Script/Extension process creation.
