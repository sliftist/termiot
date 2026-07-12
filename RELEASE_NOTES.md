## Features

### Persistent per-tab scroll position
- Each tab now remembers where it was scrolled, so switching away and back — or resuming a session after a restart — lands on the same content instead of jumping to the bottom.
- The position is stored per tab and persisted to disk (`-1` while pinned to the live bottom, otherwise counted as lines up from the bottom), so it survives the terminal buffer being rebuilt from the log.
- Scrolling is now anchored to the top visible row rather than an offset from the bottom, and scrolling all the way down automatically re-pins the view to follow live output again.
- When older history is loaded and prepended above a scrolled-up view, the view stays anchored on the same lines instead of sliding as the earlier output arrives.

### Off-thread, debounced autocomplete
- Autocomplete candidates that touch the filesystem are now recomputed on a background thread a short moment after typing stops, so directory enumeration never blocks keystrokes.
- Pressing Tab still works instantly: if the background result hasn't landed or is for older input, candidates are computed synchronously for that deliberate press.
- Locally installed executables from `node_modules\.bin` are now offered as completions alongside `package.json` scripts after `yarn`.
- The yarn-name cache is serialized behind a lock, since it is now read from both the background candidate thread and the UI thread.

### Command history in raw-key mode
- Programs that take over the keyboard (raw-key/win32-input mode, which has no input box) now contribute typed command lines to history, so recall works there too.
- The line is reconstructed from keystrokes — printable characters accumulate, Backspace pops, and a plain Enter commits it to history.
- Anything that makes the reconstruction unreliable — pasted text, arrow/edit keys, Ctrl/Alt chords, a modified Enter, or a line past a keystroke cap — taints the line so it is dropped rather than stored incorrectly.

### Redesigned window chrome
- The window name, readouts, and settings/reload buttons now sit on a slightly darker panel so their bounds are visible against the tab strip.
- An unnamed window shows no blank gap where its name would be — a faint "name" hint (and click target) appears only while hovering the chrome or editing the field.

### Steadier tab strip layout
- Tab headers hold a little extra width so small title changes don't reflow the rows, and a title whose width keeps jumping is pinned to its recent-larger width for a while so it stops thrashing the layout.
- Header widths are now measured while headers are still attached to the layout, so tabs correctly shrink back down after a long title goes away.

## Bug Fixes

### Restored session used the wrong input encoding on resume
- On resume, the `?9001` win32-input-mode enable sequence is sent only once at startup, so it lived only in old scrollback rather than in the live tail being parsed.
- Raw-key encoding could be wrong until that old history finished parsing, mis-sending modified keystrokes right after a session came back.
- The mode is now recovered from the old scrollback (and persisted per tab) and applied before the live tail parses, unless a later toggle in the live tail overrides it.

### Opening a folder from Cursor/VS Code lowercased the drive letter
- Cursor/VS Code's `Uri.fsPath` lowercases the Windows drive letter, so a folder opened into Termiot arrived as e.g. `d:\repos` instead of `D:\repos`.
- The shell's working directory then didn't match the real path casing.
- The drive letter is now restored to uppercase both in the editor extension and when the open request is received, so the cwd keeps its true casing.

### Pasted text leaked into command history
- In raw-key mode, pasted content was treated the same as typed input while a command line was being reconstructed for history.
- A paste could therefore be captured as a bogus history entry.
- Pastes now taint the current reconstructed line so it is not stored.
