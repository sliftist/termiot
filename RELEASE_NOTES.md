Features

### Two-lane masonry tab layout
- Multi-row tab strips now pack tabs into a two-lane masonry: short tabs (title only) slot into the gap beside tall tabs (title plus resource bars), so a run of short tabs fills both lanes next to a tall one instead of leaving empty space beneath the first row.
- A tall tab spans the full height of its band while short tabs drop into whichever lane is currently shorter, keeping the strip compact; when no tall tabs are present it cleanly degrades to a plain single-row wrap.
- The window chrome (name, readouts, settings/reload) now overlays the top-right corner and the tab area flows around it, so tabs no longer slide underneath the controls.

### Resource bars auto-hide on quiet tabs
- Each tab now shows its VRAM/CPU/memory bars only when its usage is significant, both relative to the heaviest tab and above an absolute floor, making idle tabs shorter and narrower.
- Hysteresis keeps a shown tab's bars from flickering off at the threshold boundary, and the global "show tab resources" toggle still governs the whole strip.

### Multi-line command input
- The input box now accepts Enter to insert line breaks, so multi-line commands and messages can be composed and pasted directly.
- When sending to a plain shell, a multi-line entry submits each line in turn.

Bug Fixes

### Pasted line breaks submitting mid-message in win32 apps
- Pasting multi-line text into the input while a win32 app like Claude was focused sent a raw carriage return partway through the text.
- The stray carriage return read as a submit, cutting the message off before the rest of the pasted content was entered.
- Pasted line breaks are now normalized to `\n` (matching Shift+Enter), so the full text is delivered as one message.
