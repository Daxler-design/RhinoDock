# Repository guidance

Read [CONTRIBUTING.md](CONTRIBUTING.md), [development](docs/development.md), and [architecture](docs/architecture.md) before changing code. Use the [user guide](docs/user-guide.md) and [Script contract](docs/scripts.md) for product behavior.

- Write version-controlled content in English.
- Read affected files and callers before editing; make focused changes that follow existing patterns.
- Keep one active Script. Lists pass whole; do not introduce implicit matching, iteration, or a graph runtime.
- Keep `rhDock.Core` independent of RhinoCommon, Eto, and Grasshopper compile-time references. Rhino document and UI behavior belongs in `rhDock.Rhino`.
- Release all Script-owned types, instances, assemblies, callbacks, reflection objects, and results with their collectible generation.
- Keep optional persistence failures non-blocking. Do not overwrite unreadable Workspace data without preserving it.
- Add a reproducing check for bug fixes and run applicable existing checks. Report live Rhino validation separately from builds and browser checks.
- Update public guides when behavior changes. Do not add development diaries, internal planning logs, or local acceptance evidence to this repository.
- Do not commit build artifacts, runtime data, secrets, or local agent configuration.
