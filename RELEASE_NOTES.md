## Features

### Per-tab resource usage

- Adds an optional resource row above each tab title showing dedicated GPU memory, CPU, and memory for everything running in that tab, with mini bar gauges scaled to the window's heaviest tab so the culprit reads at a glance.
- Measures the tab's entire process tree (shell host, shell, and everything it spawned), with CPU averaged over the last minute and GPU memory read from the Windows "GPU Process Memory" performance counters — no extra dependencies or drivers.
- Enabled from settings via "Show per-tab resource usage".

### Claude Code auto-resume

- Adds a one-click settings button that installs a Claude Code SessionStart hook, so a restored or resumed tab automatically reattaches the exact Claude session that was running in it.
- The hook writes `claude -r <sessionId>` into the tab's `AUTORESUME.cmd` whenever a session starts, using the existing per-shell auto-resume mechanism.
- Settings shows whether the hook is installed and disables the button if Claude Code isn't present.

### Per-tab command history

- Changes command history to be per-shell: up-arrow and history-based autocomplete now only surface commands that were run in that tab, instead of a single global history shared across every tab.
- Each shell's history lives in its own folder on disk, so it travels with the shell across restores.

### Configurable scrollback

- Adds a "Scrollback history (lines)" setting (100 to 10,000,000) controlling how many lines each tab keeps.
- Applies live to open tabs; shrinking takes effect as new lines scroll off.

### Render FPS display

- Adds an optional title-bar readout showing painted frames per second, the average cost of a full render, and the frame rate that cost could sustain.
- Frame counting is output-driven, so an idle terminal legitimately reads 0 fps.

## Bug Fixes

### New tabs opened on the wrong screen with spanned displays

- With a spanning setup like NVIDIA Surround, opening a tab from the editor extension (`Ctrl+Shift+C`) could land in a Termiot window on a different physical panel.
- Windows merges spanned panels into one logical monitor, so the monitor-handle comparison used to pick a "same screen" window always matched, and plain recency chose the target.
- Changes window selection to compare window rectangles instead: a window whose horizontal span substantially overlaps the active window's is treated as on the same physical screen and preferred.

### Rebuild button shown in released builds

- The title-bar rebuild-and-reload button appeared even in downloaded release builds, where no source tree exists.
- Clicking it could never work, since the recorded repo path doesn't exist on the user's disk.
- Changes the button to only appear when the build's source tree is actually present.
