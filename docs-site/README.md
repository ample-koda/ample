# The AMPLE documentation site

The static site behind https://ample-koda.dev: a music course, a guide to using the language, and a page for every block. No framework and no server logic: the build writes plain files, and routes are hash routes (`#/learn/music/basic/chords`), so any web server can serve them.

## What is here

| Path | What |
|---|---|
| `src/` | the site: `index.html`, `style.css`, `app.js` (home, philosophy, the visual and sound modes), `site.js` (routes, block pages, navigation), `previews.js` (the sound players) |
| `content/` | all pages as Markdown with a few directives (`{{pv ...}}` for a sound, `{{amplet}}` for a song, `{{ample ...}}` for the "In AMPLE" box) |
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

Rendering needs a built AMPLE command line from this repository.

## Page directives

Markdown has no fenced code blocks here; use `{{amplet}}`. A sound is one line of JSON: `{{pv {"title":"..","src":"pad","fx":"reverb","p":{"mix":0.5}}}}` (sources: `pad pluck arp bass drums noise voice tone seq chords grid meter rhythm`). Titles take no apostrophes. A label in `{{ample ...}}` takes no comma.

## Tests

```
cd test && npm install
node smoke.mjs && node click.mjs && node back.mjs
```
