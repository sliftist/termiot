## Features

### Explorer and direct launches open their own window

- Changes `--open` launches (Explorer's "open here") and plain launches to always open a new Termiot window instead of adding a tab to the most recently used live window.
- Only the Cursor extension still routes into an existing window as a tab, via the local open-tab endpoint; every other process launch deliberately gets its own window.

### Cursor extension auto-update

- Changes the installed Cursor extension to refresh itself automatically: on startup, Termiot compares the installed `package.json` and `extension.js` against the current build and reinstalls them if they differ.
- The refresh runs on a background worker alongside the stable-launcher update check, so it adds no startup cost; Cursor picks up the new extension files on its next restart.
- A failed refresh is logged rather than interrupting startup.

### Tab-routing diagnostics

- Changes same-screen window routing to log its decisions: when a reference window exists, each candidate window's horizontal span and its stacked/elsewhere verdict are written to the `route` log, making it possible to see why a tab landed in a particular window.
- Also tightens the same-column check to fetch the reference window's rectangle once per scan and to reject candidates with degenerate (zero-width) rectangles.
