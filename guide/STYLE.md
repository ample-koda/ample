# Doc style: the house style of the AMPLE guide

Rules for everything written in `guide/` (pages, dictionary, captions, screenshots) and for how the built guide looks. They apply to people and to agents. When a rule and a page disagree, fix the page. When a rule is wrong, change the rule here first, then the pages.

The reference help (`docs/`) has its own, more technical tone; take over only the rules about **terms, names and screenshots** from this file.

## 1. Who the guide is for

A person who makes music and is **new to AMPLE and maybe to music theory**. They are clever and they are learning. The guide explains how to think about AMPLE; the reference help says exactly what each setting does.

- Never assume a word is known. First use of a term is a dictionary link (automatic); if you need a word that is not in the dictionary, add it (section 6).
- Never explain by listing options. Explain one idea, show it, then say where to go for the rest.

## 2. Voice

- **Plain and warm.** Talk to "you". Short sentences. One idea per paragraph.
- **Say what happens, not what the system is.** "Press Build: AMPLE makes the sound of every block that is out of date", not "The build subsystem performs incremental rendering".
- **Name things the way the screen does.** The button says "Make this the main version", so the guide says that, and mentions the technical word (promote) once, in the dictionary.
- **Numbers and units are real.** Tempo 100 means a bar of 2.4 seconds. Give the sum when it teaches.
- **No fluff:** no "simply", "just", "obviously", "easily", "of course". No exclamation marks. No jokes that need a joke to explain.
- **Avoid mannered writing:** no dashes used as asides, no "not X, but Y" openings, no colon-then-reveal sentences, no scare quotes around invented labels.
- **Language:** English, UK or US, but one spelling per page. Dutch comes later as a translation of the same files (`pages/nl/`), never as a mix inside one page.

## 3. Structure of a page

1. Front matter with `title` (two to five words, a noun phrase or "How to...") and `summary` (one sentence, what the reader can do afterwards).
2. `# Title`, then at most two short paragraphs of introduction. The first screenshot or example comes before the first long explanation.
3. `##` sections of at most 8 or so paragraphs. Sections that are steps are numbered lists; sections that are ideas are prose.
4. A page ends with one sentence that points to the next page. The pager at the bottom is automatic.
5. A page teaches **one** topic and is readable in five minutes. Longer? Split it.
6. Order pages from what you see to what you can change: look, build, count, keep. Number the files `NN-name.md`; the name (without number) is the link name and never changes after publishing.

## 4. Formatting

- Headings: sentence case ("Build, play and stop"), no trailing punctuation, no numbering in the heading itself.
- **Bold** for what is on the screen (a button, a tab, a menu): **Build**, **Source**. `Code font` for text the reader types or sees in the song text: `at 1 for 8`, `from chords.notes -> notes`. *Italic* only for a new word on its first use when it is not in the dictionary (rare).
- Song text and commands go in fenced blocks. The song in the guide is always the song of `guide/shots/guide-shots.spec.ts`; if they differ, change the song in the shots, not just in the text.
- Callouts (a block quote that starts with a bold word): **Tip** (a shortcut or an extra), **Try it** (something to do right now, at most one per page), **Note** (a fact that surprises), **Careful** (something that can lose work). Use few: two per page is a lot.
- Tables for facts that have two or more columns (a tempo table). Lists for steps and for parts. Never a table for layout.
- Links: to a page by name `Build, play and stop`, to a dictionary word `(glossary#bar)` only when the automatic link is not enough. Link text says where it goes, never "click here".
- Keys are written like this: Space, Ctrl+B. Buttons and tabs use their exact label on screen.

## 5. Screenshots

- They are made by the script (`npm run guide:shots`), never by hand and never edited in an image program. A picture that cannot be made by the script does not belong in the guide.
- One idea per picture. Crop to the part that matters (the header, the bottom panel); show the whole window only on the first page.
- **Numbered red frames** (`annotate`) mark what the text refers to; the text explains the numbers in the same order. Use at most six numbers in a picture.
- The picture shows the **clean project** with the small demo song: no private files, no real names, no toasts or notices (they are hidden by the script).
- Caption: one sentence that says what to look at, not what the picture is. "The header: Build, the progress bar, Play, Stop and Loop." Alt text is the caption.
- Name files `topic-what-it-shows` in lower case with hyphens (`build-failed-log`). A renamed or removed picture is removed from `guide/img/` too (the check warns about unused ones).
- Before and after pairs use the same crop so the difference is the only thing that moves.

## 6. The dictionary

- One entry per idea. The word is lower case, singular, as a reader would look it up (`bar`, not `Bars`). Aliases cover plural forms and other names the screen or music uses.
- **The first sentence is the tooltip.** It must make sense alone: "A bar (also called a measure) is one small chunk of a song, the unit that music is counted in." Plain words, no word that needs another lookup unless that word is in the dictionary too.
- After the first paragraph: what it looks like in AMPLE (a real line of text, a real button), one worked example when numbers are involved, and `see:` for two or three related words.
- Say the AMPLE meaning when it differs from music theory ("in AMPLE almost every bar has four beats").
- Do not add an alias that is a very common word ("level", "input") unless the term really is that word on screen: every use would turn into a link. Check the result in the built page.
- A term that is not used on any page yet is allowed (a warning), but remove it when the topic is dropped.

## 7. The look of the built guide

The look lives in `guide/assets/guide.css` and is one system. Change tokens, not single rules.

- **Reading first.** One column of at most 880 px, line length about 70 characters, body text 16 px with a line height of 1.65, system sans-serif fonts so it loads at once and works offline.
- **Colour tokens** (light, with a dark twin under `prefers-color-scheme`): background, text, dim text, line, panel, accent blue (links, current page, term underline), red (errors, frames in screenshots), green (success, "Try it"), tip, note and warning backgrounds. Greys lean slightly blue. New colours are added as tokens to both themes; no literal colour inside a component rule.
- **Accent used with restraint:** links, the current page in the menu, dotted term underlines and the tooltip edge. Red is for errors and for the numbered frames of screenshots only.
- **Words from the dictionary** have a dotted underline and a help cursor; hovering or focusing shows the tooltip card, clicking opens the entry. They are real links, so the keyboard works (Tab, Enter) and Esc closes the card.
- **Figures:** screenshots sit on a panel with a soft shadow, a caption below, and open full size on click. Never put text in an image that matters; the text is in the page.
- **Responsive and touch:** under 820 px the menu is a top bar with a **Menu** button that opens the list of pages (tap targets at least 44 px high; a tap on a page or Esc closes it); without JavaScript the list simply stays visible. Nothing scrolls sideways except a wide code block or table inside its own box (tables are wrapped automatically). On a touch screen the first tap on a dictionary word shows its meaning and the second tap opens the entry. Every change to the look is checked at 390 px wide (the phone test in `guide.spec.ts`).
- **Motion:** none beyond smooth scrolling to an anchor. Respect reduced motion.
- **Accessibility:** every image has alt text, contrast meets WCAG AA in both themes, focus is visible, headings are in order (one `h1`, then `h2`, `h3`), the page works without JavaScript except the tooltip cards.

## 8. Keeping it true

- A page is **wrong as soon as the program changes** and it is not rebuilt. After a change to the interface: run `npm run guide:shots`, look at every changed picture, fix the text, run `npm run guide:check`, then `npm run guide:build`, and commit `guide/` and `docs/guide/` together with the code change.
- Dates are not written in pages (they go stale). The commit shows when.
- Features that do not exist yet are not described in the guide. The reference help may say "planned"; the guide only teaches what works.
- The check must pass with no errors before a commit (`guide:check`; the tests run it on the real guide).

## 9. Checklist for a new page

1. Does it teach one thing, for someone new? Is the first picture or example above the first long paragraph?
2. Are all buttons, tabs and keys written as on the screen?
3. Is every new word in the dictionary, with a good first sentence?
4. Does every picture come from the script, with a caption that says what to look at?
5. Do the numbers add up (tempo, bars, seconds)?
6. `npm run guide:check` has no errors; the warnings are understood.
7. The next-page sentence is there.
