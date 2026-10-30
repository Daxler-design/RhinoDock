# Development

## Prerequisites and build

Use the .NET 8 SDK and Node.js 22 with npm. Rhino 8.31 or later using .NET 8 is required for live validation and Yak packaging. The frontend is React/TypeScript with Vite, Tailwind, and shadcn/ui.

From the repository root:

```sh
cd frontend
npm ci
npm run build
cd ..
dotnet build rhdock.sln -c Release
```

The Rhino assembly embeds `frontend/dist/index.html`. Rebuild the frontend after UI changes, then rebuild the plugin. A clean .NET build generates the frontend if it is missing. The frontend build verifies that its inlined script parses as a classic IIFE; a successful Vite dev preview alone cannot establish this.

Plugin output: `src/rhDock.Rhino/bin/Release/net8.0/rhDock.Rhino.rhp`. Quit Rhino before replacing a loaded plugin. To compile independently of locked outputs, use `dotnet build rhdock.sln -c Release --artifacts-path .artifacts`.

## Automated checks

After a Release build:

```sh
dotnet run --project tools/smoke/smoke.csproj -c Release --no-build -- --core-only
dotnet run --project tools/smoke/smoke.csproj -c Release --no-build -- --agents-offline
dotnet run --project tools/smoke/smoke.csproj -c Release --no-build -- --agent-host
dotnet run --project tools/smoke/smoke.csproj -c Release --no-build -- --ui-bridge
dotnet run --project tools/smoke/smoke.csproj -c Release --no-build -- --plugin-metadata src/rhDock.Rhino/bin/Release/net8.0/rhDock.Rhino.rhp
cd frontend
npm run check:bridge
npm run check:mirror
```

Core checks cover Script execution, persistence, reload, and generation release. Offline agent and host checks use fixtures; they do not require agent accounts or prove live agent behavior. UI bridge checks cover delivery and lifecycle behavior without Rhino.

If only a newer .NET runtime is installed on macOS, run the built smoke DLL with `DOTNET_ROLL_FORWARD=Major dotnet tools/smoke/bin/Release/net8.0/smoke.dll --core-only` (substitute another check option as needed).

Integration mode compiles and discovers Scripts from an existing repository without editing it, then checks the packaged starter:

```sh
dotnet run --project tools/smoke/smoke.csproj -c Release --no-build -- "<loaded-repository>" "<path-to-RhinoCommon.dll>"
```

Live agent checks use `--agent <id>` or `--agent-host <id>`. They invoke an installed, signed-in agent and may consume account usage. Run them deliberately; keep private conversation logs out of the repository.

## Browser preview and mirror

Run `npm run dev` in `frontend/` for a sample-data browser preview. `?agent-stress=1` supplies a long-message layout fixture; `?history-stress=1` supplies 500 messages for backward paging and bounded-body rendering. Neither exercises native Rhino interaction.

For a live mirror, start Vite, open the panel in Rhino, run `RhinoDockMirror`, and enter Vite's port. Open the same loopback URL with `?mirror=1`. Browser controls act on the real Rhino document. Use one Rhino per port and keep the server on loopback. Run the command again to stop; closing or hiding the panel also stops the mirror.

## Packaging

After restoring and building:

```sh
dotnet msbuild tools/package-yak.proj
```

The target delegates to StageYak, validates matching versions and freshly built plugin/CLI hashes, includes license notices, and builds a local `.yak` in `.artifacts/yak/`. It uses Rhino 8's Yak tool at its standard installation path; override `YakPath` for a custom installation. Keep the version in `src/rhDock.Rhino/rhDock.Rhino.csproj` and `tools/yak/manifest.yml` synchronized. This target does not publish.

Install either the Yak package or a direct `.rhp`, not both. Restart Rhino after installation. Existing assembly names and plugin GUID must remain stable.

## Live validation

Use disposable fixtures and check Windows and macOS separately. Before calling a runtime change verified, exercise the affected workflows in Rhino:

- New/Browse, Script selection, scalar/list inputs, Run, and Auto.
- Source reload, compile-error recovery, signature changes, and stale-output removal.
- Geometry and Sub-object picks, Preview, Bake/Undo, document switches, and panel reopen.
- Solver Solve/Pause/Step/Reset and input changes.
- Workspace restore, missing picks, save warnings, and fresh-Run gating.
- Docking, narrow layouts, offline panel startup, and repeated reloads.
- For Agent changes: sends, permissions, questions, switching Conversations, cancellation, and supported rewind/Undo.

In the pull request, state platform, Rhino version, revision, expected/actual results, and which checks were unavailable. Builds, browser previews, and saved Run Records are not substitutes for live validation.

## Repository hygiene

Run `git diff --check`. CI also checks English repository content, builds the production frontend and .NET solution, and runs portable smoke checks. Do not commit generated outputs, local CLI configuration, logs, credentials, or `.rhdock/`.

## Additional panel and host checks

The frontend includes `check:script-controls`, `check:lists`, and `check:theme`; Trace activity checks run with `node scripts/check-activity.mjs`. Host access and CLI catalog checks use:

```sh
dotnet run --project tools/smoke/smoke.csproj -c Release --no-build -- --host-tools src/rhDock.Cli/bin/Release/net8.0/rhdock.dll src/rhDock.Rhino/bin/Release/net8.0/rhDock.Rhino.rhp
```

Keep native acceptance separate: panel focus versus Rhino Undo/Redo; independent input/output master preview; automatic mesh colors versus custom fill/edges and opacity; enum clearing; history prepend/selection; and concurrent Rhino instances. See [publishing](publishing.md) for the maintainer's PowerShell release flow.

## Source snapshots

`snapshot.json` records the source revision, selected source/build file hashes, and public-specific adaptations. Public guides, workflows, manifest URL, and licensing files are maintained in this repository. Source snapshots append to this repository's history rather than importing a separate development history.

## Windows subprocess handles

Run `dotnet run --project tools/smoke/smoke.csproj -c Release --no-build -- --windows-handles` on Windows. It creates disposable inheritable file handles and verifies that agent shim/native launches do not retain them after parent release; redirected pipes, environment, working directory, short-lived exits and failure cleanup are covered. It skips on other platforms. Live model save/overwrite/Save As checks still require Rhino.
