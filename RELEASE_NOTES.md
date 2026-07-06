## Features

### Open Termiot from Cursor / VS Code

- Adds a Cursor integration so Ctrl+Shift+C opens a Termiot tab in the current workspace folder — either through a bundled editor extension or by registering Termiot as the external terminal, switchable from Settings.
- The extension sends the request to a local loopback-only HTTP endpoint in a running Termiot, so a tab appears instantly with no process spawn or shell window; if nothing is running it falls back to launching the app.
- Open-tab requests route to a live window on the same monitor as the editor (falling back to the most recently used window) via WM_COPYDATA, so the tab lands where you're working.

### Explorer, Start menu, and default-launch integrations

- Adds an "Open in Termiot" entry to Explorer's right-click menu for folders, folder backgrounds, and files (files open their containing folder), plus a one-click Start menu shortcut — all per-user, no admin prompt.
- All registrations point at a version-stable launcher (`termiot.bat`) that always forwards to the newest build, so integrations never go stale after an update.
- Launching with a meaningful working directory now opens a tab in an existing window when one is alive, skipping a cold start entirely.

### Reopen windows after shutdown or crash

- Adds a "Reopen windows at Windows startup" setting that restores every window that wasn't deliberately closed — crashes, power loss, and OS shutdowns all bring your terminals back.
- Windows now record whether they were closed cleanly; only clicking X counts as deliberate, so session-ending shutdown closes still restore.
- A new Windows tab in Settings lists every window on disk — running or closed — with its tabs, and lets you reopen any closed window on demand.

### Natural-language LLM commands via trigger phrases

- Adds configurable trigger phrases (e.g. "hey llm, …") that run LLM autocomplete on demand even with the toggle off — type a phrase followed by a plain-English request and get back the command(s) that accomplish it.
- Matching is case-insensitive and letters-only ("Hey, llm" matches "heyllm"), phrases are `|`-separated and editable in Settings, and the LLM toggle highlights while a trigger is active so it's clear why suggestions appear.
- The cost readout now also shows the total request count, and requests are counted the moment they're sent so in-flight calls are visible immediately.

### Clickable links in terminal output

- Ctrl+Click any printed URL to open it in your browser; hovering with Ctrl held highlights the link and shows a hand cursor.
- Plain text URLs are detected by scanning the screen — no escape-sequence hyperlinks required — and URLs that hard-wrap across lines are joined back together before matching, so long links stay clickable end to end.

### Scriptable window and tab targeting (`--ensure`)

- Adds `--ensure --window <name> --tab <name> [-d <dir>] [--cmd <command>]` to create or reuse a named window and tab and run a command in it — missing windows are created, missing tabs are added, and existing tabs are taken over (the old shell is killed and the new command runs).
- Windows get a user-editable name in the title bar that serves as the `--ensure` lookup key; `--order` keeps scripted tabs at consistent positions, with focus following the highest-ordered tab so concurrent startup scripts end predictably.

### Smarter window placement and focus

- New windows now center over whatever window you launched them from instead of appearing at a default position, and explicit launches (Cursor, Explorer) take keyboard focus while restores surface on top without stealing it.
- Focus stealing works around Windows' foreground lock with retried activation attempts, since launchers often re-assert their own focus right after spawning.

### Tab strip and input polish

- The + button now rides inline right after the last tab, with scroll arrows and a pinned + appearing only when tabs overflow; activating a tab scrolls it into view.
- Typed-but-unsent input is persisted per tab, so reloads and window takeovers no longer lose what you were typing.
- Tab now cycles through history-based completions even when LLM autocomplete is off, and shells that enable win32-input-mode show a ⌨ badge on their tab instead of cluttering the title.

## Bug Fixes

### + button ignored the current directory

- Click the + button while the active tab is in a project directory.
- The new tab opened in the user profile folder instead of alongside your work, even though Ctrl+T used the current tab's directory.
- The + button and Ctrl+T now share one action: a new tab in the active tab's directory.

### Timers survived window close

- Close a window whose process keeps running briefly (e.g. during a takeover).
- Save, render, and sync timers kept firing against the closed window, and the cursor-blink timer kept running for detached terminals.
- All window timers stop on close, and the blink timer now stops when the terminal control unloads and restarts when it loads.
