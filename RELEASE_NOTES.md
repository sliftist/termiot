Features

## Live search that keeps up with output

- Changes search to stay current as the terminal produces new output — matches now appear the moment new lines are written instead of freezing at whatever was on screen when you opened the search bar.
- Committed scrollback is scanned once and cached; only newly-committed lines and the volatile live screen are rescanned on each render tick, so keeping search open no longer means re-scanning the entire buffer on every frame.
- Rescans are throttled and index-shift aware — when scrollback is trimmed at its cap or prepended, absolute line indices change and the cache is rebuilt from scratch to stay correct.
- The highlight follows the newest match only when your cursor was already sitting on the last match; otherwise your scroll position is left untouched so a passive refresh never yanks the view.

## Search navigation buttons

- Adds ↑ and ↓ buttons to the search bar for stepping to the previous and next match with the mouse.
- Mirrors the existing keyboard flow (Enter for next, Shift+Enter for previous) and keeps focus in the search box after a click so keyboard navigation continues uninterrupted.

## Double-click and triple-click selection

- Changes mouse selection so a double-click selects the whole word under the cursor and a triple-click selects the entire logical line.
- Words are letters, digits, and underscore — punctuation such as periods and slashes ends a word — so clicking inside a path or identifier grabs just the token you meant.
- Triple-click spans wrapped rows, selecting the full logical line even when it flowed across multiple visual rows.

## Clear command history

- Adds a "Clear command history" item to the tab context menu that forgets every remembered command for that shell and deletes its backing file (recreated on the next command).
- When clearing the active tab, the up-arrow history position and autocomplete candidates reset immediately.

## Clearer active-filter styling

- Changes the search bar's filter toggle to turn green — text, border, and background tint — when engaged, so it reads as unmistakably active instead of the previous subtle inset look.

Bug Fixes

## Copying wrapped lines inserted spurious newlines

- Selecting text that spanned a line the terminal had wrapped edge-to-edge into the next row.
- Copying it pasted a hard line break at each wrap point, splitting what was really one continuous line — breaking long commands and paths when pasted elsewhere.
- Changes copy to only insert a newline at genuine line breaks, treating an edge-to-edge wrap as the visual continuation it is.
