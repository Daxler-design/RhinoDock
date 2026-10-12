# Known limitations

RhinoDock is experimental. Automated checks cover host-independent behavior; they do not prove full Rhino compatibility on every platform.

- **Rhino blocking.** Scripts and Solver frames run on Rhino's UI path, so a long `Run(...)` blocks interaction.
- **Dependencies.** RhinoDock compiles C# with Roslyn. It does not run MSBuild or restore NuGet packages; add DLLs through `<Reference>` with a `HintPath`.
- **Multiple Rhino processes.** Two processes writing one repository do not merge Workspace saves; the last save wins.
- **Agents.** Capabilities, sign-in, and saved permissions vary by agent, and some agents have no approval prompts. Only some agents have been tested live (Claude Code, Codex, OpenCode, Pi, and Hermes Agent); Cursor, Grok Build, omp, fx, and Antigravity have not.
- **Possible Rhino exit with Claude Code.** On macOS, Rhino exited while sending a prompt to Claude Code after an effort change. Version 0.5.9 removed the suspected cause, but this is not confirmed fixed. Use disposable work when testing that path.
- **Rewind.** Checkpoints are bounded text snapshots, not backups. Use Git.
- **Extensions.** They run with full trust and are not sandboxed. A crash, a native library, or a stuck UI thread can end Rhino. Distribution is by sharing a repository; there is no signing or review.
- **Platform coverage.** Native WebView behavior, sub-object picking, and some display controls need more verification on both operating systems.

Report a reproducible problem with the RhinoDock version, Rhino version, operating system, expected result, and actual result. Remove private content from logs first.
