## Features

### Always-on win32 input mode
- Adds an **Always use win32 input mode** setting (on by default) that sends full keyboard records to every shell, so Ctrl+Enter, Shift+Enter, and Alt chords are never lost — including across resume and restart.
- When turned off, input mode reverts to auto: win32 records are only sent once an app requests win32-input-mode (`?9001`), matching the original behavior.
- The effective mode drives paste encoding, key forwarding, and text input consistently across live and restored tabs.

### Per-tab raw-keys mode
- Raw-keys mode (keystrokes going straight to the shell instead of the input box) is now tracked per tab rather than globally, so switching tabs restores each tab's own mode.
- New tabs inherit the last-chosen raw-keys setting as their default, and the toggle mirrors whichever tab is active.
- The mode is persisted per shell and restored on resume.

### Clearer keyboard-mode indicator
- The `⌨` win32-input badge is now always shown on every tab — bright blue when the mode is active, dim when off — so the current keyboard fidelity is unmistakable instead of silently absent.
- Its tooltip states the active mode (full keyboard fidelity vs. legacy VT input).

### Clear console history
- Replaces "Clear command history" with **Clear console history**, which wipes a tab's scrolled-back output while leaving the current prompt and on-screen output in place.
- For live tabs, the host truncates its saved log files so the cleared scrollback doesn't reappear on resume; dead tabs have their logs truncated directly.
- Command markers and in-memory scrollback are cleared together, with search and scroll stats refreshed immediately.

### More responsive UI under heavy state and logging
- Small state files (shell metadata, window layout, settings, last-active records) are now written on a dedicated background thread and coalesced per file, so bursts of saves never block the UI thread; pending writes are flushed to disk before the process exits or hands off to a takeover/rebuild.
- Application logging is likewise moved off the caller's hot path onto a background writer that batches bursts, and is flushed on crash so nothing is lost.

### Refreshed active-tab styling
- The focused tab now has an orange fill with a brighter orange underline, making it unmistakable against the alternating dark shades of inactive tabs.
- A transparent underline is reserved on every tab so the active tab's accent doesn't change its height.

### Wider link detection
- Clickable links now recognize `ftp`, `ftps`, and `file` URLs in addition to `http`/`https`.

## Bug Fixes

### Copying wrapped lines inserted spurious newlines
- Copying (or link-joining) text that had wrapped across the right edge relied on a "is this line full width" guess to tell a soft wrap from a real newline.
- Trimmed scrollback lines always looked full, so wrap boundaries were misdetected — copied text gained or lost newlines and multi-line link detection joined the wrong rows.
- Each line now carries an explicit wrapped flag set at the moment it flows onto the next, which survives trimming and the push to scrollback, so copy and link-join reflect the real line structure.

### Tab-completion walk broke mid-cycle
- Stepping through completions with Tab/Up/Down re-derived the candidate list from the input box on every step.
- Because each step swaps the box text to a candidate, later steps recomputed against the completion instead of what was originally typed, breaking the walk.
- The basis text and candidate list are now fixed for the whole cycle and only re-derived when starting a fresh walk (after a manual edit or running a command).

### Huge directories flooded the completion list
- Requesting completions with an empty token in a very large directory returned tens of thousands of entries.
- This bloated the candidate list and slowed the UI-thread merge with results nobody would cycle through.
- Filesystem completions from a single directory are now capped at 500.

### Multi-line suggestions shifted the whole UI
- A completion candidate containing newlines rendered as multiple rows in the suggestion panel.
- The panel grew and the entire UI shifted as such candidates appeared while typing.
- Suggestion rows now collapse newlines to a `⏎` marker and never wrap, keeping each candidate on one row while still inserting the full multi-line value on selection.
