---
title: Hear it while you turn it
order: 80
lead: The live chain plays your effects in the browser while you change them, so you hear a knob move before anything is rebuilt or written.
---
## The live chain

Open the **Advanced** window of an effect, mixer or bus block and press **Live chain** under *Compare / render*. AMPLE takes the built sound of the sources that lead to this block and loops it through every effect and mixer on the way, in the browser. The song text and the build are not touched.

{{shot live-chain | The live chain bar | One chip per block: press a chip to bypass that block (it fades, it stays in the chain). Each block has a stereo meter; the bar also shows the latency of the chain and a CPU meter with a safe mode.}}

- Turn a knob and you hear it at once. The song text is written when you let go, so **Undo** takes the move back in one step.
- **Bypass** a block with its chip to hear the chain without it, then press it again.
- The **loop range** limits the loop to one to eight bars. In a long song it also decides where the live window starts (see *Long songs*). The spectrum and the scope show the sound before and after a block.
- **Stop** ends the loop. Nothing is saved or built by the chain itself.
- On the **Mixer** tab, **Hear live** on a strip plays the faders and pans while you drag them; they are written when you release.

Most blocks sound exactly like the build: the reverb, gate, limiter, de-esser, distortion, delay, EQ, parametric EQ, flanger, phaser, pan, width and volume (also a volume that moves with `auto`, `lfo`, `env`, `fadein` or `fadeout`) run the same calculation in the browser as in the build. The tremolo and the sweeps of the chorus, flanger, phaser and filter start with the loop, like the build, and the chorus keeps its stereo width. The chorus and the filter are close; the build is always the truth.

## What the bar tells you

{{shot live-chain-tools | The tools of the bar | The spectrum of one effect, before and after, under the chain bar.}}

- **Latency** shows how late the sound is: the audio system of your computer plus the look-ahead of the blocks in the chain (a limiter). Hover it for the parts. When you compare with the build, the built sound is delayed by the same amount so both line up.
- **CPU** shows how busy the audio thread is. Over 70 percent it turns amber. With **Safe mode: auto** (the default) a load that stays over 90 percent bypasses the costliest effect and tells you; the chip says *Bypassed (CPU)* until you switch the block on again yourself. Browsers without a load meter show an estimate (CPU ~N%) that never bypasses anything.
- The **~** button on an effect chip opens a **spectrum** of the sound before (gray) and after (accent) that block, or a **scope**.
- If you change a block upstream after its sound was built, the chain keeps playing the old build and says *Out of date* until you build it again.
- Selecting an effect warms up the chain in the background (the built sounds are loaded), so **Live chain** starts without waiting.

## Play it with your hands

- **MIDI-learn**: Shift + right-click a knob (long press on a touch screen, or press L while it has focus), then move a knob on your MIDI controller. The knob follows it from then on; the song is written when you stop moving, as one undo step. Do the same on a bound knob to clear it. This needs Web MIDI (Chrome or Edge), and the bindings are kept in this browser only.
- **Snapshots**: in the menu of the Advanced window (the three lines), click A, B, C or D to store the current values, click a stored one to recall it. The **Morph** slider blends two snapshots (frequencies in a musical, logarithmic way) and writes once when you let go.

- **Convolution reverbs in the chain**: a convolution block can sit in the live chain; its impulse response is loaded when the chain starts, and choosing another impulse (or changing the length) crossfades to it without a click.
- **Pumping against a key**: connect a block to the *key* input of a compressor and the compressor pumps against it live. (The duck block has its own pattern and no key input.)
- **Drums play hit by hit**: the built-in drums in the live chain are single hits started on the audio clock, so changing the pattern or song is heard from the next hit without the short wait. Drums that feed a cut or reverse still re-render.
- **Live notes on every panel**: panels with their own controls (parametric EQ, volume, mixer) list, behind the small **i** in the title bar, what moves live, re-renders or needs a build (for example "live: gains, pan").

## Which parameters move live?

A small dot on every knob says whether it moves live (hover it). For the panels that draw their own controls (peq, volume, mixer, bus) the small **i** in the title bar says which parameters move live, which re-render and which need a build.

{{shot live-badges | The live notes of a parameter equaliser | "live: 30 parameters"; the tooltip names them.}}

## How close is it?

Press **Check live vs render** to play a test sound through the live version of a block and through the browser render and see how far apart they are. The badge says *exact*, *close*, *approximate* or *differs*, with the distance in dB. A small badge by each knob says whether it **moves live**, **re-renders** after you let go, or **needs a build**. **A/B vs build** switches a block between the live sound and the take that was built.

{{shot live-parity | The check of live against render | The badge under Compare / render says how close the live sound is to the browser render.}}

## More blocks in the chain

Since 0.1.17 more blocks sound in the live chain without a build. Built-in synth, piano, bass, guitar and an inline sequencer play their clip and are made again a moment after you change a knob or a note. `click`, `join`, `crossfade`, `splitter`, `normalize` and `pitch` work on the sound that comes in and are swapped in at the same place of the loop. The `master` block shows its loudness in LUFS and the distance to its target. A `compressor` with a `key` input ducks live, a `send` feeds its effect, and a `curve` (with `map`) moves a parameter while the loop plays. A block that a model made (ace-music, ai-voice, voice-clone, choir) plays its stored take and the bar tells you when that take was made.

Also in the chain without a build: `trim` and `silence`, `stretch`, `granular`, and `denoise` (made again a moment after you stop turning), and a `midi` file with an audio output. General MIDI notes of the piano roll and of those blocks are played by the browser itself from the General MIDI SoundFont (about 215 MB, downloaded once and kept); you can switch that off in the browser with `localStorage["ample.sf2.local"] = "0"`.

The **master** in the chain is the real limiter, with a loudness chip (LUFS) that shows how far you are from the target. A **compressor** can take a key: connect a block to its key input and the compressor ducks against it while you listen. A **send** has its two outputs, `through` and `send`, in the chain.

The `delay` block has a new `sync` setting (`1/8`, `1/16t`, ...) that locks the echo to the tempo.

{{try Hear a chain}}
Build a song with a drum or sample block, an `eq` and a `reverb` connected in a row. Build the source, open the Advanced window of the reverb, and press **Live chain**. Turn the high band of the eq up and down while the loop plays, bypass the reverb with its chip, then press **Check live vs render**.
{{/try}}

The live chain needs a browser with Web Audio worklets (any current Chrome, Edge, Firefox or Safari); a message tells you when something is missing.

## Long songs

A song longer than about two minutes does not hear all of itself live: the strip plays a window of the song and the badge says for example "previewing bars 41-89 of 120". The window starts at the first bar of the loop range you set; two small arrow buttons on the bar move it forwards and back. Build the block to hear the whole song.

In a window that does not start at bar 1, `cut` and `chop` with a list of times are only approximate live; the build is exact.
