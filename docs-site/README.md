# The AMPLE documentation site

The static site behind https://ample-koda.dev: a music course, a guide to using the language, and a page for every block. No framework and no server logic: the build writes plain files, and routes are hash routes (`#/learn/music/basic/chords`), so any web server can serve them.

## Source of truth

This folder, `docs-site/` in the repository, is the only source of the documentation site. The old workspace `workspaces/docs-mockup` (outside git) is not used any more and must be ignored: do not edit it, do not build from it and do not copy files back from it (owner decision, 2026-11-13). Pages, the build, the pictures and the sounds all live here; a release builds from this folder (`node build.mjs`) and uploads `site/`.

Pictures of AMPLE on a page: `{{shot name | what it shows | caption}}` shows `get-site/assets/shots/<name>.png` (made by `tools/screenshots`, one copy). The build copies them to `site/shots/` and stops with MISSING PICTURES when a page names one that does not exist.

## What is here

| Path | What |
|---|---|
| `src/` | the site: `index.html`, `style.css`, `app.js` (home, philosophy, the visual and sound modes), `site.js` (routes, block pages, navigation), `previews.js` (the sound players) |
| `content/` | all pages as Markdown with a few directives (`{{pv ...}}` for a sound, `{{amplet}}` for a song, `{{ample ...}}` for the "In AMPLE" box, `{{shot ...}}` for a picture of AMPLE) |
| `lib/md.mjs` | the Markdown and directive renderer |
| `lib/amplet.js` | turns the description of a sound into an AMPLE song (used by the build, the browser and the sound renderer) |
| `build.mjs` | the generator |
| `tools/render-samples.mjs` | writes every sound of the site as an AMPLE song, checks it and renders it to an mp3 |
| `vendor/codemirror-ample.js` | the highlighter for AMPLE code shown on the pages |
| `samples.json` | what each sound is: its id, its AMPLE code, its length and what rendered it |
| `test/` | small browser checks (Playwright) |

The block pages come from the language data in `spec/templates/`: ports, parameters, ranges, the example song and what each block needs. When the spec changes, the pages change with the next build.

## Build

```
node build.mjs --status   # what is new or changed since the last build (writes nothing)
node build.mjs            # writes site/ (open site/index.html, or serve it over http)
```

The sounds only play from the rendered mp3 over http(s); from `file://` the pages fall back to a rough sound made in the browser.

## Sounds

Every sound on the site is an AMPLE song with `master-seed 20261017`, rendered by the AMPLE command line to `site/audio/<id>.mp3` (the id is a hash of what the sound is). Each one lasts exactly 2, 4 or 8 seconds so that layered sounds stay in time.

```
node tools/render-samples.mjs --check             # check every song (fast)
node tools/render-samples.mjs --render            # render the new and changed ones
node tools/render-samples.mjs --render --force    # render everything
```

Rendering needs a built AMPLE command line from this repository, and `ffmpeg` / `ffprobe` on the PATH (for the example songs).

### Example songs of the block pages

The "Example song" of every block page (the `example` field of `spec/templates/<block>.json`) is a complete song and has a player above its code. `tools/render-samples.mjs` renders each one with the real command line to a WAV, checks it (not silent: RMS above -60 dB and peak above -40 dB; not clipping: peak below -0.05 dB; a failing song is reported as failed and gets no mp3), and encodes `audio/ex-<hash>.mp3` (the hash is the same 12 character hash of the song text as for the other sounds; comment lines do not count). A song longer than 45 s is cut to its first 45 s with a fade out, and the page says so. `samples.json` lists each one with `example` (the block), `bpm`, `seconds`, `stats` (peak, RMS, full length, cut) and `built`.

Blocks whose example needs an AI or plugin model (`ace-music`, `ai-voice`, `choir`, `scorer`, `voice-clone`) are skipped and have no player. Blocks that need files (`ambience`, `import-audio`, `sampler`, `midi`, `midi-render`, `gm`, `guitar`, `convolution`) are rendered (`lyrics` makes no sound and has no player; `gm` and `midi-render` use the default MuseScore General SoundFont, MIT, from the installed packs, `ample packs install soundfont`) in a real project of their own (`render/ex-<block>`, made with `ample new`) that holds the free CC0 files of `example-assets/` at the paths the song names; `example-assets/examples.json` lists them per block, and may replace text of the spec example or append text; the spec is not changed. The files and their sources and licences are in `example-assets/ATTRIBUTION.md`; the build copies them to `site/example-assets/` and the page shows them as download links above the code (`Open in Remix` is hidden for those examples: Remix cannot carry the files). `bass` and `piano` use built-in sounds (no files), `drums` (built-in kit) and `sequencer` (the sequence is in the song) need nothing. The tool prints the skipped blocks.

When the spec changes, run `node tools/render-samples.mjs --render` (only songs whose text changed are rendered again; the build then picks the new `ex-<hash>.mp3` up by itself, see `exAudio` in `build.mjs`), then `node build.mjs`. A changed example without a new render has no player until it is rendered. `node tools/render-samples.mjs --verify` shows what is outdated.

An example song is exclusive: starting it stops every other sound and the music, and starting any other sound or the music stops it. It plays once (no loop) and keeps playing over page changes, like the other previews. The browser check is `test/example.mjs`; `tools/boot-check.mjs` checks that every mp3 an example points to exists.

## Page directives

Markdown has no fenced code blocks here; use `{{amplet}}`. A sound is one line of JSON: `{{pv {"title":"..","src":"pad","fx":"reverb","p":{"mix":0.5}}}}` (sources: `pad pluck arp bass drums noise voice tone seq chords grid meter rhythm`). Titles take no apostrophes. A label in `{{ample ...}}` takes no comma.

## Tests

```
cd test && npm install
node smoke.mjs && node click.mjs && node back.mjs
```
