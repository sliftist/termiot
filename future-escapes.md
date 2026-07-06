# Escape sequences we don't support yet

Sequences the parser currently recognizes but deliberately ignores (or doesn't recognize at all), collected so we can prioritize eventual support. The parser consumes all of these safely — "unsupported" means the behavior they request doesn't happen, not that they leak onto the screen.

## Seen from ConPTY on every session (highest priority)

- ~~`ESC[?9001h` / `l` — **win32-input-mode**~~ — SUPPORTED in raw-keys mode: special/modified keys and typed characters are sent as full Win32 key records (`ESC[<Vk>;<Sc>;<Uc>;<Kd>;<Cs>;<Rc>_`), preserving modifier state VT can't express (Ctrl+Enter vs Shift+Enter vs Enter). Down/up records are emitted as immediate pairs; real key-held timing isn't forwarded yet.
- `ESC[?1004h` / `l` — **focus reporting**. When enabled, send `ESC[I` on window focus gained and `ESC[O` on focus lost. Trivial to implement: track the mode bit, hook `Window.Activated`/`Deactivated`, write the two sequences to the pty.

## Input-related modes

- `ESC[?2004h` / `l` — bracketed paste: wrap pasted text in `ESC[200~` … `ESC[201~` so shells can distinguish paste from typing. Relevant once we implement paste at all.
- `ESC[?1000h` / `?1002h` / `?1003h` / `?1006h` — mouse reporting (click, drag, move, SGR encoding). Needed for full-screen apps (vim, htop) to receive mouse events.
- `ESC=` / `ESC>` — application keypad mode (DECKPAM/DECKPNM). Changes what the numpad sends.
- `ESC[?1h` / `l` — application cursor keys (DECCKM): arrows send `ESC OA` instead of `ESC[A`. Full-screen apps set this and may mis-read our arrow keys until supported.

## Rendering

- `ESC[ q` (DECSCUSR) — cursor shape/blink (block, underline, bar). We always draw a block.
- `ESC[?12h` / `l` — cursor blinking. We don't blink.
- SGR 3 (italic), 5 (blink), 8 (conceal), 9 (strikethrough), 21 (double underline), 53 (overline), 58/59 (underline color). We render bold, dim, underline, reverse, and full color.
- `ESC(0` — DEC Special Graphics charset (line-drawing characters via `ESC(0` + `j`–`x`). We consume the designation but never translate; legacy apps drawing boxes will show letters instead of lines.
- Wide characters (CJK, emoji): everything renders one cell wide; double-width glyphs will overlap.
- `ESC[3J` handled (clears scrollback), but `ESC[?5h` (reverse video screen mode) and `DECSCNM` are ignored.

## OSC (operating system commands)

- OSC 0/2 supported (window title). Unsupported:
- OSC 4 / 10 / 11 / 12 — redefine palette colors, default fg/bg, cursor color.
- OSC 7 — shell reports its working directory. Once cmd is configured to emit this, it's a far more robust cwd tracker than our prompt-regex.
- OSC 8 — hyperlinks (clickable links with explicit targets).
- OSC 9;4 — taskbar progress (ConEmu/Windows Terminal convention); OSC 9 notifications.
- OSC 52 — clipboard read/write from the shell.
- OSC 133 / 633 — shell-integration marks (prompt start, command start, command end + exit code). Would let us delimit commands in scrollback, jump between prompts, and capture per-command output.

## Queries we don't answer (we answer DSR 5/6 and DA1)

- `ESC[>c` (DA2, terminal version), `ESC[=c` (DA3), `ESC[>q` (XTVERSION).
- DECRQSS (`DCS $ q … ST`) — status string requests; all DCS content is currently discarded.
- XTGETTCAP, DECRQM (`ESC[?…$p` — mode state queries).

## Other

- HTS (`ESC H`) / TBC (`ESC[g`) — custom tab stops; we hardcode every 8 columns.
- IRM (`ESC[4h`) — insert mode.
- DECAWM (`ESC[?7h/l`) — autowrap toggle; we always wrap.
- Sixel / iTerm2 / kitty image protocols (DCS-based) — discarded.
