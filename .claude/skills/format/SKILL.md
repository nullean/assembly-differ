---
name: format
description: Format code with curb, or fix formatting failures in CI. Use when the user asks to format, when `mise run check` fails, or before committing C# changes.
---

# Format

[curb](https://curb.nullean.net) formats C# from `.editorconfig`. Its defaults match the IDE's, so formatting in an
editor and with curb agree.

```bash
mise run format   # rewrite files in place
mise run check    # exit 1 if anything would change (CI runs this)
```

- `.editorconfig` comes from `nullean/.github` and is identical in every repo. Change it there, not here.
- Re-formatting a whole repo goes in its own PR, labelled `chore`, so behaviour PRs stay readable.
- If curb produces output that looks wrong, do not hand-format around it: report it at
  https://github.com/nullean/curb/issues with the input and the `.editorconfig` section.
