# The AMPLE guide (the learning help)

The **guide** teaches how AMPLE works, with pictures and a dictionary. It sits next to the **reference help** (`docs/`, built into `docs/help/help.html`, opened with the Help link): the reference says exactly what every block and setting does, the guide explains how to think about it. Both stay; neither replaces the other.

Open it in a running AMPLE with the **Guide** link in the header, or at `/guide/`. It is plain static files in `docs/guide/` (committed), so it also works without a server.

The house style (voice, page structure, formatting, screenshots, dictionary entries and the look) is in [STYLE.md](STYLE.md): read it before writing or changing anything here.

## How it is made

```
guide/
  pages/NN-name.md     the pages, in file name order (front matter: title, summary)
  glossary.md          the dictionary: one "## word" per term
  img/*.png            the screenshots (made by guide/shots, never by hand)
  shots/               the program that makes the screenshots
  assets/              guide.css, guide.js (the look and the tooltips)
tools/guide/           the builder (build.mjs), the Markdown converter (md.mjs) and its tests
docs/guide/            the built guide (generated: do not edit)
```

| Command | What it does |
|---|---|
| `npm run guide:shots` | Starts a real AMPLE on a clean project and takes every screenshot again (needs `dotnet build ample/Ample.sln` first). |
| `npm run guide:build` | Builds `docs/guide/` from `guide/`. |
| `npm run guide:check` | Checks without writing: missing screenshots, broken links, unknown terms; exit code 1 on errors. |
| `npm run test:guide` | The tests of the builder and a check of the real guide. |

After a change: edit the sources, run `guide:check`, then `guide:build`, and commit `guide/` and `docs/guide/` together. When the program looks different, run `guide:shots` first and look at the pictures.

## Writing a page

A page starts with front matter, then Markdown:

```
---
title: Bars and time
summary: One line that shows on the start page and under the title.
---

# Bars and time
...
```

- Links to other pages use the page name without number and extension: `Build, play and stop`; to a heading `(build-play-stop#build)`; to a dictionary entry `(glossary#bar)`.
- Callouts: a block quote that starts with **Tip**, **Note**, **Careful** or **Try it** (in bold).
- Code and song text go in fenced blocks. Keep the song text in the guide the same as the song of the screenshots (`guide/shots/guide-shots.spec.ts`).
- A screenshot is a line of its own: `![[shot:name|Caption]]`. The caption is optional; the file must exist as `guide/img/name.png`.

## The dictionary

Every term is a section in `guide/glossary.md`:

```
## bar
aliases: bars, measure
see: beat, tempo
A first sentence that says what it is (the tooltip shows the first paragraph).

More text, with `code` and links if needed.
```

- **Automatic links.** The first time a term (or an alias) appears on a page, outside code and headings, it becomes a dotted link with a tooltip; a click opens its entry. You never mark words by hand. Choose aliases with care: a very common word as alias links all over the place.
- **Used in.** Every entry lists the pages where the word is linked, built automatically.
- `see:` names other terms (checked: an unknown name is an error).
- A word that is defined but used on no page yet only gives a warning.

## Screenshots

Every picture is described once in `guide/shots/guide-shots.spec.ts`: the song, what to click, which parts get a numbered frame (`annotate`). Messages (toasts) are hidden so pictures show the program, not its notices. To add a picture: add the steps, run `guide:shots`, look at the result, and reference it from a page with `![[shot:name]]`. The check warns about pictures no page uses.

## Ideas for later

More pages (lyrics and voices, the arrangement and recording, branches and compare, modules, the guitar and General MIDI instruments), a search box, a Dutch translation, and `?` buttons in the program that open the dictionary at the right word.
