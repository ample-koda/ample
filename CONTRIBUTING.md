# Contributing

AMPLE does not take contributions yet. This repository is being filled step by step, and every
line that appears in it is reviewed by the author first.

Pull requests and issues from others are not accepted yet. When that changes, this page will say so and will
explain how contributions are licensed (they will be under the same MIT License as the project).

## Line length

A diff is read in a narrow column, so a line of several hundred characters is hard to review. These limits apply to the lines a change **adds or edits**. Lines that are already long are left alone; nothing has to be reformatted.

| Files | Guideline | Limit |
|---|---|---|
| `.js` `.mjs` `.cjs` `.ts` `.cs` | 140 | 200 |
| `.css` | 140 | 200 |
| `.html` | 160 | 240 |

- A line over the **guideline** is a warning. A line over the **limit** fails the check.
- A call or object literal with more than three properties goes one property per line. A CSS rule with more than two declarations goes one declaration per line, and a selector list one selector per line.
- A condition that is written twice gets a name (`const busy = a || b;`). A nested ternary or a text longer than about 60 characters goes into a `const` above the line.
- A trailing comment that makes the line too long goes on its own line above it.
- A URL, a `data:` URI and an `import` or `export` line may be long. A table whose rows are meant to be one line each is marked with `long-lines-ok` and the reason, on the line or the line above.
- Generated and third-party files (`vendor/`, `codemirror/`, `docs/help/`, `docs/api/`, `*.min.*`, lock files, `bin/`, `obj/`) are not checked.

Check your change before you open a pull request:

```
node tools/check-long-lines.mjs            # lines added since origin/main
node tools/check-long-lines.mjs --strict   # the guideline fails too
```

Thank you for your interest.
