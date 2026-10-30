# Agent tab

The Agent tab connects a coding agent you already have installed. The agent keeps its own sign-in, models, tools, and stored conversations; RhinoDock adds the Rhino context and a review surface.

## Choose an agent

Pick an installed agent from the header switcher. Supported agents: Claude Code, Codex, Cursor, Grok Build, OpenCode, Pi, omp, fx, Hermes Agent, and Antigravity (macOS only). Install and sign in with the agent's own instructions; RhinoDock shows which ones it found, and you can point it at a custom executable.

Capabilities vary by agent, and RhinoDock hides controls an agent does not support. Pi and omp have no approval protocol, so their tools run without asking.

## Work with the agent

- **Context.** The active Script can be attached to a prompt, along with files and images.
- **Streaming.** Replies stream as Markdown. Similar tool activity is grouped into collapsible Trace rows; active and failed work stays visible. Expand rows to inspect diffs, commands, output, and images.
- **Permissions.** Each request asks to *Allow once*, *Always allow*, or *Reject*. Reading is automatic; edits and commands follow the permission mode you choose.
- **Plans.** A plan from plan mode appears as a proposal. Build it with or without asking first, or reply to refine it.
- **Questions.** The agent can ask you multiple-choice or free-text questions.
- **Steering.** Press Enter during a turn to add a message after the current tool call; Cmd/Ctrl+Enter queues it for after the turn.
- **Comments.** Select text to reveal the comment/side-chat toolbar while preserving ordinary copying. Comments retain an anchor to the passage and can be edited before submission. Open a side conversation for a separate question; native harness forks are used where supported.
- **Rewind and Undo.** Rewind a conversation to an earlier prompt and restore files; Undo brings it back. These are bounded conveniences, not a replacement for Git.
- **Models and effort.** Switch model and effort from the composer; Claude Code applies a new effort without restarting.
- **Search.** Ctrl/Cmd+F finds text or jumps through your prompts.

## Conversations

Conversations continue in the background while you use another one or the Script tab. The list shows each one's state: waiting for you, working, unread, or failed. A badge on the Agent tab counts conversations that need you. Titles come from the agent when it names conversations; Codex conversations get one short generated title after the first turn.

## Safety

An agent can edit files and run commands with your permissions. Read the [security guidance](https://github.com/Daxler-design/RhinoDock/blob/main/SECURITY.md). Agent settings, saved permissions, and MCP servers belong to the agent's own configuration; RhinoDock shows MCP calls as tool rows but does not manage them. Enable rhDockConnect explicitly when you want the agent to control Rhino; see below.

## Rhino access with rhDockConnect

Choose rhDockConnect from the slash menu, or submit `/rhdock-connect`, to enable access for the whole Rhino instance. All chats, harnesses and external CLI clients running as the same user can use that instance. Disabling it from any Connected row revokes every client. Access survives chat/harness disposal, panel closure and document/repository changes; it ends when disabled or Rhino exits, and starts disabled after restart.

The switch enables both read and write tools. Harness permission modes remain with the harness; the bridge has no second approval card and does not enforce a harness's Plan mode against direct write requests. Calls remain authenticated and validate their document/repository target before execution. A stale queued request is rejected instead of operating on the newly active document.

For an external Windows terminal:

```powershell
$rhdock = "$env:LOCALAPPDATA\rhDock\host-tools\bin\rhdock.cmd"
& $rhdock instances
& $rhdock --pid RHINO_PID status
& $rhdock --pid RHINO_PID tools
& $rhdock --pid RHINO_PID describe get_context
& $rhdock --pid RHINO_PID call get_context
```

Replace RHINO_PID with the intended enabled instance. The launcher is written when the plugin loads; global PATH is unchanged. The CLI verifies process start time so an old descriptor cannot silently select a reused PID. `status` returns document/repository details and an opaque target. For several commands on the same document, retain that target and pass `--target TARGET` on each call/resource. Inspect fresh state if it changes. Reuse the request ID, target and arguments after an uncertain mutation instead of issuing a replacement blindly.

In-panel agents receive updated discovery instructions on each send/steer. External agents need the entry instructions once; [standalone Rhino access instructions](RHDOCK.md) can be attached without editing a main AGENTS.md. Optional `--source VIEW_ID` routes tool/image cards to an open conversation; source labels grant no authority. Every dispatched call records PID, source, request ID, tool and status in the host log.

The CLI provides Script state, input/Run/Preview/layout operations and integrated Rhino/Grasshopper tools adapted from McNeel RhinoAI. No MCP configuration is installed; unrelated MCP servers remain managed by your harness.

Where supported, the composer also exposes Claude Code Auto or Codex Auto-review. These are native review modes and differ from Allow all.

## Images and history

Images from tools, supported local file links, and attachments appear in the conversation. Click a thumbnail to enlarge it, then zoom, pan, or save. A thumbnail can have lower resolution than its source; what the agent actually receives depends on the tool and harness.

The panel initially publishes the latest 100 history items and loads older pages when you scroll upward or choose Load earlier messages. Nearby message bodies are mounted while offscreen bodies use measured placeholders. Replayed history remains in host memory; this does not change the model's context or the harness's stored conversation. Search covers the currently loaded history.

The file-review tray labels the current Git worktree and branch. Its changes remain conversation checkpoints rather than the full `git status`; use Git for repository history.
