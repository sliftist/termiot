## Features

### Instant session restore
- Restoring a window with heavy shells is dramatically faster: the recent tail of each tab renders immediately, and older scrollback fills in behind the scenes without blocking anything.
- The scrollback is now read directly from the persisted log file instead of streaming megabytes over the host pipe; the host only sends a small recent slice, which is overlap-joined onto the file read.
- Older history is parsed on a background thread into a scratch screen and prepended as scrollback, one tab at a time — the focused tab loads first, and background tabs wait until it's done (or until you switch to them).
- Shell output logs now rotate between two capped files, so retained history stays bounded and replay never has to chew through an oversized log.
- A brief "Loading…" overlay holds the terminal during the replay burst, so history appears in one paint instead of animating frame-by-frame.

### Multi-row tab strip
- Tabs now wrap onto as many rows as they need instead of scrolling horizontally, so every tab stays visible.
- Tab headers keep a sticky width when their titles shrink slightly, preventing constant row reflow as command names come and go.
- Drag-and-drop reordering works across rows, with the drop indicator landing at the correct slot on whichever row you're over.
- A "Single-row tabs" setting restores the previous scrolling behavior.

### Rebindable hotkeys
- Window shortcuts (new tab, close tab, next/previous tab, new window, search, restart shell) can be rebound in a new Hotkeys tab in settings.
- Settings store only your overrides as gesture strings (e.g. `Ctrl+Shift+Tab`); anything untouched keeps its default, and matching is exact on modifiers so overlapping combos can't shadow each other.

### Run shells as administrator
- A tab's context menu now has a "Run as administrator" option that restarts the shell elevated via a UAC prompt.
- Only the shell host process elevates — the window itself stays unelevated — and a 🛡 badge on the tab shows when the connected host reports it's running elevated.
- The preference is sticky per tab, so the shell comes back elevated across restarts and restores.

### Search filter mode
- The search bar gains a "filter" toggle that shows only matching lines plus the lines indented under each match, collapsing everything else.
- Blank lines don't break a match's indented subtree, so structured output (stack traces, tree listings) stays intact under its matching header.

### Multi-line command input
- The input box now wraps and grows to fit multi-line text; Shift+Enter or Ctrl+Enter inserts a newline while plain Enter submits.
- A new setting swaps the behavior (Enter inserts a newline, Ctrl/Shift+Enter submits).
- The box is pinned to exactly the height its wrapped text needs, eliminating the one-line height jitter WPF's caret padding used to cause.

### Terminal scrollbar
- The terminal gains a slim scrollbar in its own gutter along the right edge.
- PageUp/PageDown now scroll the scrollback, unless a full-screen app is active — those still receive the keys themselves.

### More reliable shell restart
- Restarting a shell now force-kills the old host and waits until it's actually gone before spawning the replacement, instead of hoping a graceful shutdown finishes in time.
- The restart button shows a progress icon while the restart runs, and the new shell re-runs the tab's AUTORESUME.cmd if it has one.

### Rebuild feedback and fast reload
- The rebuild-and-reload button now streams the build output into a log shown in its tooltip and in the settings window, and a wedged build is killed after a timeout so the button can never get stuck on the hourglass.
- Shift+Click reloads the window onto the newest already-built binary without rebuilding.

### Stale dead tabs aren't restored
- Tabs whose shell died well before the window was closed are treated as abandoned and dropped on restore, instead of reappearing as resume prompts.
- Each shell records when it was seen dying; on restore that timestamp is compared against the window's close time, with a grace period so shells that died alongside the window still come back.

### Scrollback stats and diagnostics
- The status row shows the current tab's scrollback size (lines and characters).
- Settings gain Startup and Profiling tabs showing the window's startup timeline and per-tab restore timings.

## Bug Fixes

### Enter submitted as a paste in TUI apps
- With a TUI app using paste detection (e.g. Claude Code) running in win32-input mode, typing a command in the input bar and pressing Enter inserted the text with a trailing newline instead of submitting it.
- The app treated the single burst ending in `\r` as a paste containing a newline, so commands never ran.
- The text is now sent first and a genuine Enter key record follows after a short delay, so it reads as its own keypress.

### Ctrl+C cleared the input instead of copying
- Select text inside the input box and press Ctrl+C.
- The selection wasn't copied — the keypress fell through to the clear-input/interrupt ladder, wiping what you'd typed.
- Ctrl+C with an input-box selection now copies that selection to the clipboard.

### Editing shortcuts forwarded to the shell
- With the input box focused, pressing Ctrl+Left/Right, Ctrl+Home/End, Ctrl+A, Ctrl+X, Ctrl+Z/Y, or Ctrl+Backspace/Delete.
- The chord was forwarded to the shell instead of editing the input, so word navigation, select-all, cut, and undo didn't work while composing a command.
- Standard text-editing chords now stay with the focused input editor.

### UI stalls from title-change floods
- Reconnect to a shell whose history contains thousands of OSC title changes (common with prompt frameworks).
- Every title change dispatched to the UI thread individually, stalling the window during replay.
- Title changes are now coalesced — the render tick applies only the latest title once per frame.

### Command history blocked the UI on slow disks
- Open a window whose per-tab history files live on a slow or network drive, or press Enter to run a command there.
- History was read and appended synchronously on the UI thread, so window construction and the Enter keypress could stall on disk.
- All history disk access now runs off the UI thread, with commands run before the load finishes kept newest.

### LLM autocomplete echoed your own input
- Type a command with LLM autocomplete enabled.
- The model often suggested exactly what you'd already typed, or the same suggestion multiple times, wasting the suggestion slots.
- Suggestions matching the typed text and duplicates are now filtered out.
