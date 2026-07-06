## Features

### Command History Walking with Up/Down

- Adds shell-style history navigation to the input line: pressing `Up` swaps the previous command directly into the input, and repeated presses walk further back through history.
- Treats the input line as the "center" of navigation — `Up` moves into history above, while `Down` moves into the autocomplete suggestion rows below; when already in the suggestion rows, `Up` cycles back through candidates.
- Stashes whatever you had typed before starting a history walk, so pressing `Down` past the most recent entry restores your original unsent text.
- Resets the history walk on any edit or programmatic text change, so typing always returns you to the center position rather than leaving you stranded mid-history.
