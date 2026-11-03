# 10 Lyrics format

**Status: only partly specified.** The own lyric format `.amplelrc` (all lyric information in one extensible structure: words, line and word timing, syllables, sounds, singers, language, notes; it states `format: amplelrc` and a version) is decided in principle (decisions, "Lyric formats") but its field-by-field layout is still open design work (open-questions).

## What exists (provisional)
The sketch's `lyrics` block points at a plain file (`lyrics/chorus.lyr`) with **one line per row**, written `[bar 9] text`. The `file` is the canonical source: the Lyrics editor reads and writes it, and linked AI voice or ACE music blocks receive its marker-free words. The old `text` parameter remains as a compatibility fallback only when the file is absent or empty; it is not edited by the Lyrics editor. The editor checks live: the first line within 5 seconds, lines in order, lines outside the block's placement, empty lines, long lines, too many words for the gap.

## Importers (planned, not part of the grammar)
`.lrc`, enhanced `.lrc`, `.srt`/`.vtt`, TextGrid and aligner JSON convert to and from `.amplelrc`.

## Plan
This page becomes normative when `.amplelrc` is designed: a JSON Schema in `../../spec/schema/`, a conformance corpus under `../../spec-tests/lyrics/`, and the checks listed above as diagnostic codes.
