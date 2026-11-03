# 18 - Comments, doc comments and the hover

Status: built. What a comment can be, how a helper, a constant or a block says what it is for, and what the editor shows when a name is pointed at.

## 1. Comments

| Form | Meaning |
| --- | --- |
| `# text` | a comment to the end of the line (a `#` right after a letter or digit is not a comment) |
| `/* text */` | a block comment: it can stand inside a line or cover many lines |

A block comment is read as blanks (the lines, columns and offsets stay the same), so everything else in the file reads as it did. A `/*` inside a string, after a `#`, or right after a letter or digit (`4/*2` is a division) starts nothing. A comment that is never closed is **AMP1116** (it would swallow the rest of the file). The document keeps the comments: the editors and the edits see the original text; `# ample-ignore CODE` works as before.

Limits, by design: a block comment cannot be nested, and tools that read the raw text without the parser (the replace and rename edits) still see code that is commented out with `/* */` as code.

## 2. Doc comments

A doc comment is `/** ... */`, or lines that start with `##`, **directly above** (no blank line) a helper, a `let` constant, a type alias or a block:

```
/**
 * Moves a voice up and delays it.
 * @param {Interval|Int} by how far up, in semitones
 * @param Notes source the notes to copy
 * @param delay when the copy starts
 * @return {Notes} the copy, shifted
 * @example theme.notes |> canonVoice(+7st)
 */
helper canonVoice(Notes source, Interval by, Duration delay = 2bars) -> Notes { ... }

## The bass line of the chorus.
## @deprecated use bass2 instead
midi-clip bass at 1 for auto { ... }
```

| Tag | Meaning |
| --- | --- |
| text before the first tag | the summary |
| `@param [type] name text` | what a parameter means; the type is optional (below) |
| `@return [type] text` (or `@returns`) | what the helper gives back |
| `@example code` | an example (the lines that follow belong to it) |
| `@deprecated [text]` | the name should not be used any more |

**Types and unions.** A type may be written in braces (`{Interval|Int}`, `{List<Int>}`, `{Int?}`) or bare when it is a type of the language, a union or a generic (`@param Notes source ...`). A bare first word that is not a type (`@param source the notes`) is the name. The hover shows the declared type of a parameter and adds *(documented as `Interval|Int`)* when the comment gives another one, so a comment can say more than the declaration (a union the helper really accepts) without changing the song. The type is documentation, but it is checked against the declaration (section 3).

## 3. The checks: a doc comment must not lie

Only a helper that has a doc comment is checked, so a song without doc comments gets nothing new. The warnings are reported on the declaration of the helper (so `# ample-ignore CODE` on the line above silences one) and are part of the normal check, so the assistant sees them too. They do not stop a build.

| Code | Level | What |
| --- | --- | --- |
| AMP2090 | warning | a `@param` names a parameter the helper does not have (renamed or removed) |
| AMP2091 | warning | the type of a `@param` or `@return` is not the declared one: unions are compared as sets of members, a type alias (`type Shift = Interval | Int`) is written out first, `Int?` is `Int`, case is ignored; a wider or a narrower comment is a difference |
| AMP2092 | warning | a type in the comment does not exist (a typing mistake) |
| AMP2093 | warning | a call of a helper whose comment says `@deprecated` (with the reason) |
| AMP2094 | info | the comment describes some parameters but not all |

The hover also says it: *(documented as `Interval|Int`)* next to a parameter and *(declared `Notes`)* after the returned value when the comment gives another type. A union inside the brackets of a generic (`List<Int|Float>`) is one type: it is split only at a `|` or `,` outside the brackets, the arguments are compared as types of their own (aliases and the order of a union included) and every name inside is checked for AMP2092. **Quick fixes** (on the warning, in the tip of the line and in the code actions): AMP2090 renames the `@param` to the parameter that is nearest (or removes the line when none is, which "Fix all" leaves alone because it removes text), AMP2091 writes the declared type in the comment, AMP2092 changes the typo to the nearest type, and AMP2094 adds the missing `@param` lines after the last one in the same style (`*` or `##`). A fix only changes the comment, never the code. In the browser without the language server the warnings are shown but have no fix buttons.

## 4. The hover

Pointing at a name (a short rest of the pointer in the source editor, a tap on a touch screen) shows:

| Name | Shown |
| --- | --- |
| a helper of the song | `name(Type param = default, ...) -> Type`, the doc comment (summary, deprecation, returns, examples), every parameter with its type, its default and its note, "Returns `Type`" when no comment says it, pure or dynamic, what it depends on, baked |
| a `let`, a `var`, a constant, a parameter or a loop variable (at its declaration and at every use) | `let name: Type`, its value, **Type** with how it was found (a list of notes, a number with a unit, the call of `transpose()`, arithmetic on both sides, the declared type), and for a `var` that is assigned values of other types the **Potential types** (`Int`, `Float`); a parameter also shows its `@param` note |
| the `let` or `var` keyword | what the keyword does and what it declares (`Declares motif: Notes`) |
| a constant, a type alias | its type and its doc comment |
| a block name | the doc comment above the block |
| a note operator (`shift`, `humanize` ...) | the signature (with the receiver), what it does, every parameter with type, default and note, "Built in." |
| a function (`Round`, `min`, `Note` ...), a method (`seed.fork`) | the same, from the `builtins` of the catalogue |
| `currentTime`, `osRandom`, `environment` | the signature, that it is volatile and the state of the capability |
| a unit (`2bars`, `250ms`, `+7st`) | the unit and its meaning |
| a word of a helper body (`for`, `emit`, `let`, `return` ...) | what it does |

The text comes from the language server (`textDocument/hover`, markdown); when the server cannot be reached the browser builds the same text from the catalogue and the song (`web/lib/doc-comments.mjs`, the twin of `Comments.cs` and `Queries.cs`). The notes of the parameters of the built-in operators are the `doc` of the parameters in `spec/note-ops.json` (the catalogue is the one source; run `node tools/gen-note-catalog.mjs` after a change). A built-in function, method, unit or keyword is an entry of its `builtins` section; the evaluator does not read that list, it only feeds the hover.

**The types of variables** are worked out without running anything, from the value: notes literals, pipelines and operators (the return type of the catalogue), numbers (Int or Float, and with a unit Length, Time, Interval, Gain or Percent), strings, booleans, lists (`List<Int | Float>`), ranges, helper calls (the declared return type), the built-in functions, and arithmetic (`/` gives a Float, a length times a number is a length, a length plus a time is a Duration). A declared type (`let x: Type = ...`) wins. **A helper that does not write its return type gets it from its body**: the types of every `return` (a union when they differ, `Int | Float`), `Notes` when it emits, and the type of its last expression; the hover shows `-> Type` and "worked out from what the body returns", and a call of that helper has that type for `let x = helper()`. (Three rounds are made, so a helper that calls another untyped helper gets its type too; a recursive helper must still say its type.) A call of a function that gives no type, or a word the language does not know, shows `unknown`. The same logic is in `NoteLang.Variables.cs` (server) and `web/lib/variable-types.mjs` (browser, without the language server).

**Keeping the catalogue complete.** When a built-in is added to the language (a function in `NoteLang.Interp.cs`, a unit, an operator), add its entry to `spec/note-ops.json` in the same change: an operator in `operators`, anything else in `builtins`. `CommentsTests` checks a sample of them.
