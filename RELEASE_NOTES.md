Features

### Per-tab search and filter state
- Changes search so each tab keeps its own query, filter toggle, and current match — switching tabs now restores that tab's search instead of carrying one query across all of them.
- Snapshots the live search bar (query text, filter on/off, and the highlighted match index) onto a tab as you leave it, then re-applies it against the incoming tab's own buffer so matches recompute against the right terminal contents.
- Reopening a tab whose search box still holds a query re-highlights its matches immediately rather than waiting for you to edit the query, and closing search is scoped to the current tab so it won't spring back open on the next switch.

Bug Fixes

### Holding a modifier key jumps the terminal to the bottom
- In win32-input-mode, pressing a bare modifier — Ctrl, Shift, Alt, or Win — was forwarded as a keystroke that also scrolled the view.
- Holding Ctrl to Ctrl-click somewhere in scrollback would snap the terminal down to the bottom, throwing away your scroll position.
- Changes the input path so modifier keys pressed alone are still forwarded but no longer trigger a scroll-to-bottom, keeping the view where you left it.
