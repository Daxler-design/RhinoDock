# Agent tab

The Agent tab connects a coding agent you already have installed. The agent keeps its own sign-in, models, tools, and stored conversations; RhinoDock adds the Rhino context and a review surface.

## Choose an agent

Pick an installed agent from the header switcher. Supported agents: Claude Code, Codex, Cursor, Grok Build, OpenCode, Pi, omp, fx, Hermes Agent, and Antigravity (macOS only). Install and sign in with the agent's own instructions; RhinoDock shows which ones it found, and you can point it at a custom executable.

Capabilities vary by agent, and RhinoDock hides controls an agent does not support. Pi and omp have no approval protocol, so their tools run without asking.

## Work with the agent

- **Context.** The active Script can be attached to a prompt, along with files and images.
- **Streaming.** Replies stream as Markdown, with tool calls shown as diffs, commands and output, or file contents.
- **Permissions.** Each request asks to *Allow once*, *Always allow*, or *Reject*. Reading is automatic; edits and commands follow the permission mode you choose.
- **Plans.** A plan from plan mode appears as a proposal. Build it with or without asking first, or reply to refine it.
- **Questions.** The agent can ask you multiple-choice or free-text questions.
- **Steering.** Press Enter during a turn to add a message after the current tool call; Cmd/Ctrl+Enter queues it for after the turn.
- **Comments.** Select text in the conversation to comment on it. Comments attach to your next prompt as quotes.
- **Rewind and Undo.** Rewind a conversation to an earlier prompt and restore files; Undo brings it back. These are bounded conveniences, not a replacement for Git.
- **Models and effort.** Switch model and effort from the composer; Claude Code applies a new effort without restarting.
- **Search.** Ctrl/Cmd+F finds text or jumps through your prompts.

## Conversations

Conversations continue in the background while you use another one or the Script tab. The list shows each one's state: waiting for you, working, unread, or failed. A badge on the Agent tab counts conversations that need you. Titles come from the agent when it names conversations; Codex conversations get one short generated title after the first turn.

## Safety

An agent can edit files and run commands with your permissions. Read the [security guidance](https://github.com/Daxler-design/RhinoDock/blob/main/SECURITY.md). Agent settings, saved permissions, and MCP servers belong to the agent's own configuration; RhinoDock shows MCP calls as tool rows but does not manage them. RhinoDock does not give agents built-in tools to control Rhino.
