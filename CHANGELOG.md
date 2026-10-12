# Changelog

User-visible changes. Newest first.

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
