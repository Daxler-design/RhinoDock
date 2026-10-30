# Rhino access through rhDock (Windows)

When the user requests work in a running Rhino instance, use this PowerShell entry:

```powershell
$rhdock = "$env:LOCALAPPDATA\rhDock\host-tools\bin\rhdock.cmd"
& $rhdock instances
```

- Use the user's Rhino PID, or the only listed instance. If several are listed and the target is unclear, ask which to use. If none are listed, ask the user to enable rhDockConnect in Rhino.
- Run `& $rhdock --pid RHINO_PID status`; verify the document/repository and retain `target` for this task.
- Discover commands with `tools` and inspect each tool with `describe TOOL`. Start with `call get_context` and `call rhdock_state`.
- For calls, use `--target TARGET --args-file FILE.json`; use the same target for resource reads. After a target error, inspect fresh state before continuing.
- If a mutation's outcome is uncertain, reuse its request ID, target and arguments. Inspect state before starting a replacement operation.
- Open returned image paths with your image-viewing tool. A filename alone does not mean you inspected the image.

rhDockConnect enables instance access; follow the user's task and your harness permission mode when choosing operations.
