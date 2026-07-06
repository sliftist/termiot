# Termiot

**Download:** https://github.com/sliftist/termiot/releases/latest

A fast, crash-proof terminal for Windows. Every window is its own process and every shell is its own process, connected by named pipes and persisted to disk — so a crashed or closed window resurrects with its tabs, scrollback, and even the text you had typed but not yet run. Windows shutting down? Everything comes back at next login.

![Tabs](pictures/main%20view.png)

## Features

- One process per window, one process per shell — a crash never takes anything else down, and shells outlive their window long enough to be reclaimed
- Full session persistence: tabs, scrollback (replayed from the output log), working directories, window positions, and pending input all restore from disk
- Bitmap-rendered output with a glyph atlas — no per-frame text layout
- Drag tabs between windows, or drag one to empty space to spawn a new window
- Tab and command-aware titles (directory + running command), editable per tab, with win32-input-mode and auto-resume indicators
- Selection, search (`Ctrl+F`), clickable links (Ctrl+Click, wrapped URLs included), and full modifier-distinct key input (`Ctrl+Enter` ≠ `Enter`) via win32-input-mode

### LLM autocomplete

![LLM autocomplete](pictures/autocomplete.png)

Bring your own OpenRouter key and the input line predicts complete commands from your terminal context (recent output, command history, directory):

- Multi-complete shows several candidates at once; `Tab`/arrows cycle, configurable count (1–100)
- Trigger phrases (`hey llm`, `ai please`, …) run a natural-language request through the model even with autocomplete off — "Hey AI, what's my largest disk?" becomes the command that answers it
- Context-aware regular completion too: files in the directory, command history, `yarn` scripts from package.json
- Live cost and call counter, model picker, and a "show current context" inspector in settings

### Session model

- Shells keep running when a window closes unexpectedly and reattach on restore; deliberately closed windows stay closed
- A Windows tab in settings lists every window ever recorded (running or not, with its tabs) for one-click resurrection
- Optional startup entry restores everything that was open at shutdown/logoff
- Per-shell `AUTORESUME.cmd` re-runs your command on resume

### Integrations

- **Cursor/VS Code**: a bundled extension binds `Ctrl+Shift+C` to open a tab in the workspace folder — routed over a local HTTP endpoint into an already-running window (same monitor preferred), no process spawn at all
- **Explorer**: "Open in Termiot" on folders, folder backgrounds, and files (per-user registry, no admin)
- **Start menu** shortcut and default-terminal registration
- Everything registers through a version-stable launcher bat that every build repoints, so integrations never go stale

### Scripted layouts

Launch parameters target windows and tabs by name — if the tab already exists its command is restarted, otherwise it's created in the right position:

```
termiot.bat --ensure --window startup --tab "Dev Server" -d "D:\repos\myapp" --cmd "yarn dev" --order 10
```

Startup scripts built from these converge to the same window, in a consistent tab order, with the highest-order tab focused.

## Installing

Download the latest release and run it: https://github.com/sliftist/termiot/releases/latest

## Development

1. Install the .NET 10 SDK: https://dotnet.microsoft.com/download
2. Install Node.js: https://nodejs.org/
3. Install Yarn (https://yarnpkg.com/): `npm install -g yarn`
4. `git clone https://github.com/sliftist/termiot.git`
5. `cd termiot`
6. `yarn start-instance`
