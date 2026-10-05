# RhinoDock

A live C# workspace inside Rhino 8. Open a C# repository, choose a Script, adjust inputs, and inspect geometry in the viewport. Edit and save source to reload it without restarting Rhino.

RhinoDock is experimental software for Windows and macOS. See [known limitations](docs/limitations.md) before using it on important work.

## What it does

- Runs one selected Script with scalar, list, color, geometry, and Sub-object inputs.
- Shows transient geometry Preview and explicitly Bakes selected outputs into Rhino with undo.
- Supports iterative Solver Scripts with Solve, Pause, Step, Reset, and frame-rate controls.
- Saves per-Script inputs and display settings in the Loaded Repository.
- Connects installed coding-agent CLIs through an optional Agent tab, with capabilities determined by each agent.

## Build and install

You need Rhino 8 using .NET 8 to run the plugin. To build it, install the .NET 8 SDK and Node.js 22 with npm.

```sh
git clone https://github.com/Daxler-design/RhinoDock.git
cd RhinoDock
cd frontend
npm ci
npm run build
cd ..
dotnet build rhdock.sln -c Release
dotnet msbuild tools/package-yak.proj
```

The local `.yak` package is written to `.artifacts/yak/`. Install it with Rhino's Yak CLI (`yak install <path-to-package.yak>`), restart Rhino, and run `RhinoDock`. Packaging expects Rhino 8 at its standard installation path. It does not publish a package.

Install only one copy of the plugin. If you loaded an `.rhp` directly or installed another copy, remove that registration before installing the Yak package. The packaged plugin does not need Node.js or a frontend server.

## First run

1. Run `RhinoDock` in Rhino.
2. Choose **New** to create a starter repository, or **Browse** to open an existing one.
3. Select a Script, set its inputs, and pick any required geometry.
4. Choose **Run** (or **Solve** for a Solver Script). Inspect outputs and Preview.
5. Use **Bake** when you want geometry added to the document.

The starter includes a minimal Script, Hex Attractor Scale, and Laplacian Curve Smooth. Auto Run is opt-in and starts off when reopening a Workspace or switching Scripts.

## Documentation

- [User guide](docs/user-guide.md): inputs, Preview, Bake, saved Workspaces, and agents.
- [Writing Scripts](docs/scripts.md): entry points, outputs, dependencies, and Solvers.
- [Development](docs/development.md): builds, checks, packaging, and live Rhino validation.
- [Architecture](docs/architecture.md): module responsibilities and lifetime rules.
- [Known limitations](docs/limitations.md): current constraints and unverified behavior.
- [Contributing](CONTRIBUTING.md) and [security reporting](SECURITY.md).

## Repository layout

```text
src/
  rhDock.Core/       Compilation, Script execution, persistence, starter template
  rhDock.Agents/     Agent protocols and process adapters
  rhDock.Rhino/      Rhino commands, panel, document interaction, Preview and Bake
frontend/           React and TypeScript panel
tools/              Smoke checks and Yak packaging
docs/               User and contributor guides
```

Assembly names, namespaces, the `rhdock` Yak package name, and `.rhdock` data directory retain their existing identifiers for compatibility.

## License

[MIT](LICENSE). Third-party code retains its own licenses; see [third-party notices](THIRD-PARTY-NOTICES.md). Rhino and external agent services are separate products with their own requirements.
