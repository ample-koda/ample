# 02 Lexical structure

## Text
- Encoding **UTF-8**, no BOM required (a BOM is accepted and removed by the formatter). Line endings `\n` or `\r\n`; the formatter writes `\n`.
- The language is **line oriented**: one statement per line, except a block, which opens with a header line ending in `{` and closes with `}` alone on a line.
- Positions are **1-based line and column**, columns counted in characters (code points). The language server converts to UTF-16 for editors. A diagnostic range is `[col, endCol)` on one line.

## Comments
`#` starts a comment that runs to the end of the line. A comment that reads `# ample-ignore L001, L005` silences those lint rules for the **next** block ([08](08-diagnostics.md)). Comments are preserved by the formatter.

## Names
- **identifier**: one or more of letters, digits, `_` and `-`. Examples: `drums`, `kit`, `lm_seed`, `master-seed`, `felt-piano`.
- **string**: double quoted, with the escapes `\"`, `\\`, `\n`, `\t`; any other backslash sequence stands for the escaped character itself (a parser never fails on an escape). A **name** is an identifier or a string; use a string when the name contains spaces or other characters.
- Block names are compared **exactly** (case sensitive); two names that differ only by case trigger lint rule L008.

## Numbers
`integer` is digits; `number` is digits with an optional `.digits`. There are no negative literals in structure positions (tempo, bars, seeds); a parameter value is free text and may contain a leading `-` (for example `-4 dB`). Seeds are unsigned 64-bit integers from 0 to 18446744073709551615.

## Keywords
Keywords are reserved only in their own position (a block may be named `at` if it is quoted). The list is in `../../spec/tokens.json`: `project features lint layout tempo master-seed at for as from bake lock refresh freeze seeds plan stable pinned live on off assist diff button auto info warning error`.

## Operators and punctuation
`{ } , . -> @`. `->` and `.` appear only in `from` lines; `@` only in `pinned@version`; `,` only between placement parts and inside nothing else.

## Limits (spec 0.1)
Tempo 20 to 300. Project master seeds and block seeds range from 0 to 18446744073709551615. Meter: a song is in 4/4 unless its composition line says `meter 3/4` (the top number 1 to 16, the bottom number 2, 4, 8 or 16; AMP1120 otherwise): a bar then has the quarter-note beats of that meter (3/4 and 6/8: three, 5/4: five, 7/8: three and a half), and everything that counts bars follows it (see "Meter" in 03-syntax).
