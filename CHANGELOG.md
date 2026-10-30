# Changelog

User-visible changes. Newest first.

## 0.6.11 - 2026-10-30

### Added

- Share one explicitly enabled rhDockConnect service across chats, harnesses and external CLI clients in the same Rhino instance.
- Discover enabled instances with `rhdock instances`; select one with `--pid`, inspect `status`, and pin a document/repository target with `--target`.
- Use a stable user-level CLI launcher and optional standalone [Rhino access instructions](docs/RHDOCK.md).

### Changed

- rhDockConnect stays enabled across chat/harness disposal and document/repository changes. Disable it from any chat to revoke all clients, or exit Rhino. It starts disabled after restart.
- The connection switch enables both reads and writes independently of harness permission modes. A harness's Plan mode is not enforced by the bridge against direct write requests. Authentication, stale-target checks, serialized calls and retry deduplication remain.

### Fixed

- Restrict Windows agent subprocesses to inheriting their three redirected pipe handles, preventing unrelated Rhino model handles from remaining open in agent processes after Save As. This covers the shared agent launcher and Git metadata queries; arbitrary Script/Extension subprocesses remain outside this fix.
- Preserve conversation tool/image cards through optional source labels while sharing the instance service.

### Verification

- Release/frontend builds and transport, CLI, harness and Windows handle-isolation checks passed. The maintainer reported the installed build working in their Windows workflow. This does not certify every native platform or save scenario.
- Requires Rhino 8.31 or later. Existing Working and Extension repositories do not need migration.

## 0.6.10 - 2026-10-23

### Added

- Arrange Script inputs and outputs into named groups, reorder and fold them, and control input and output previews independently.
- Select named modes from dropdowns for scalar C# enum inputs, including the Script's declared default.
- Edit fill and edge colors independently with a compact circular channel selector and separate opacity settings.
- Use rhDockConnect to give the active agent authenticated access to Script state, inputs, runs, previews, and integrated Rhino tools. The package includes the required CLI.
- See images from tool results and agent replies inline, with zoom, pan, save, and larger viewport captures on request.
- Add anchored selection comments and open side conversations, using native conversation forks where supported.
- Select Claude Code Auto or Codex Auto-review where the installed harness supports them.
- Customize the panel theme and accent color, edit list items inline, and use the ViewportSliders Extension example.

### Improved

- Group tool activity with collapsible Trace rows and show the current Git worktree and branch in the file-review tray.
- Load older conversation history in pages of 100 and mount nearby message bodies rather than rendering every loaded message body. Host replay remains in memory; this is not a host-memory limit or model-context change.
- Retain ordinary conversation text, tool details, and image references instead of dropping the oldest transcript items.
- Improve narrow-panel layouts, input descriptions, queued-message editing, and cross-instance layout refresh.

### Fixed

- Keep Script-input undo/redo separate from Rhino document undo/redo; text editors retain native text undo. Geometry picks and layout/style changes are outside input history.
- Preserve automatic mesh analysis colors with custom edges, including the partial-opacity drawing path.
- Remove the connection-lifetime host-tool request cap while retaining retry handling.
- Package the newly built frontend, plugin, and CLI together to avoid stale bundled files.
- Improve viewport capture, image previews, and color pickers inside scrolling list details.

### Compatibility and verification

- Requires Rhino 8.31 or later, using Rhino's .NET 8 runtime. Source builds also require the .NET 8 SDK and Node.js.
- Existing Working and Extension repositories are not overwritten. Enum dropdowns require enum declarations in the Script; existing numeric or string modes remain usable.
- Some native platform interactions still need verification. The latest automatic vertex-color fix is compiled but awaiting maintainer validation; do not read this snapshot as certification of every native workflow.

## 0.6.9

- Adapt Auto Run delay to measured Script run time instead of using a fixed delay for every Script.

## 0.6.8

- Now installs on Rhino 8.30 and later (was 8.35). Rhino 8.34 users can find and install it from the Package Manager.

## 0.6.7

- **Extensions.** A new Extensions tab opens an Extension Repo and enables, reloads, and creates Extensions. They hot-reload on save, open their own windows, drive the Script session, and can add editors for input types the panel lacks. See [Extensions](docs/extensions.md).
- **Mirror.** `RhinoDockMirror` shows the panel and Extension windows in a browser, with developer tools.
- **Debugging.** One shared log, page-error forwarding, an inspector on macOS, and `RHDOCK_DEBUG=1` for breakpoints.

## 0.5.10

- Claude Code changes effort without restarting.
- The Agent tab shows a badge for conversations that are unread or need input.
- The panel reattaches after you open a file in Rhino.

## 0.5.x

- Conversation titles come from the agent; unread conversations show a dot.
- Plans from plan mode are proposed for review. Select conversation text to comment on it.
- One set of permission buttons for every agent; clearer file and command names on cards.
- Conversations keep running in the background.

## 0.5.0

- Ten coding agents through one Agent tab, with a plan panel, file review, Rewind and Undo, favorites, and turn timing.
