# Known limitations

RhinoDock is experimental. Automated checks cover host-independent contracts; they do not establish complete Windows/macOS Rhino compatibility.

- A Rhino exit when sending a Claude Code prompt with Low effort has been reported and remains unresolved. Use disposable work when testing that path.
- Native WebView behavior, multiple Rhino processes, Sub-object picking, numeric/list output tables, and some display controls need further live verification across platforms.
- Scripts run on Rhino's execution path; a long `Run(...)` blocks interaction. Solver Frames should return promptly.
- Concurrent Rhino processes writing one Loaded Repository do not merge Workspace revisions; the final save controls restore.
- Loaded Repository compilation does not evaluate a full MSBuild project or restore NuGet dependencies.
- Agent capabilities, authentication, and persisted permissions vary by CLI. Some agents offer no approval protocol. Built-in Rhino Host Tools are not implemented.
- Rewind checkpoints are bounded text snapshots and are not complete repository backups.
- The portable file-watcher rename check is skipped on macOS; validate rename-sensitive workflows in Rhino.

Report a reproducible problem with the plugin revision, Rhino version, operating system, expected result, and actual result. Remove private content from fixtures and logs.
