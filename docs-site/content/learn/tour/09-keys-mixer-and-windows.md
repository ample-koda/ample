---
title: Keys and shortcuts
order: 70
lead: The keys that get you around, the mixer view for the whole song, windows side by side and a block library that remembers your favourites.
---
## The keys

Press `?` (or `F1`) anywhere to see every key on one sheet. `Esc` closes it again.

{{shot keys-sheet | The sheet of keys | Opens with ? or F1. The keys of the app are listed per group; the sheet is built from the same list the keys use, so it is always right.}}

- `Ctrl+Shift+P` opens the **command palette**: type a few letters of anything AMPLE can do and press Enter.
- `F5` the playlist, `F6` the sequencer, `F7` the Jam, `F9` the mixer.
- `F8` or `/` puts the cursor in the search box of the block library.

### Choose your own keys

On the sheet every command has a **Change** button. Press it, then press the key (or key combination) you want; **Backspace** gives the command no key and **Esc** cancels. {{shot keys-remap | A key that is already taken | Changing the mixer key to F5 shows that the playlist uses it; the old key stays until you pick a free one.}}

When the key is already used by another command, or by one of the keys that are always there, the sheet says so and keeps waiting for another key. A command with its own key has a **Reset** button that gives the default back, and **Reset all keys** clears every choice. Your keys are kept per user in this browser, and the command palette shows the same keys, because both are built from the same list.

{{shot command-palette | The command palette | Typing "v" lists every command with a v in it, each with its key.}}

## The mixer view

The **Mixer** tab of the bottom panel shows one strip for every `mixer`, `bus` and `master` block of the song. Each input of a `mixer` is a strip with the **name and colour of the block that feeds it** (also when effects sit in between), a pan knob, a tall fader with a dB scale, a meter, **M** and **S**. An input with nothing connected is dim. Everything you do on a strip is written into the song, as one step of Undo, exactly like the Advanced panel of the block does.

{{shot mixer-view | The mixer view | Each strip carries the name and colour of its source, a pan knob, a fader, a meter and the Rev and Dly sends; the master strip sits next to the Output meter and shows the loudness.}}

- Double-click a fader to put it back at 0 dB. Double-click a pan knob to put it in the middle. Pan is written as `l`, `c`, `r` or a number from -32 to 32.
- **M** mutes an input: the song gets `mutes off on` (one `on` or `off` per input) and the input keeps its gain. **S** solos it (`solos`): while any input of that mixer has a solo, only the inputs with a solo are heard. Both are written into the song, like the faders, so they come with the song to another computer.
- **Clear solo** switches every solo of the mixer off at once (every `solos` value goes back to `off`).
- The meter of each strip holds its peak as a white line; click the meter to clear the peak.
- Inputs at the end of a mixer with nothing connected are hidden; **+ 2 empty inputs** in the strip shows them. The small list above the strips chooses **All mixers** or one mixer.
- The small meter next to each fader moves while the song plays and the **Lights** are on.
- Use the list at the top left to show **one mixer block** with its inputs and its output instead of all of them.
- On a phone the strips are narrower and the row scrolls sideways.
- The **master** strip has its own fader (it sets the **limiter ceiling** of the master block, from -12 dB to 0 dB; double-click puts it back at -1 dB; the value is written into the song) and a meter that follows the song that is playing. A bus or volume strip has a fader and a meter of what the block gives too.
- The meters of the inputs are wide: a left and a right bar, so you can see them at a glance.
- The master strip also shows the **loudness**: the short-term and the integrated LUFS next to the `target` of the master block, and the peak (with hold). **Reset** starts the measurement again.

### Effects on a strip

Under the fader of a strip you see the effects that sit between the source and the mixer, one chip each. The dot on a chip switches that effect on or off (`enabled off` in the song), a click on its name opens the block, the arrows move it up or down the chain and the cross removes it. The **+** at the bottom adds an EQ, a compressor, a reverb or a delay: the block is added to the song and connected for you, and Undo takes it away again.

### Sends

The **Rev** and **Dly** knobs of a strip send part of that sound to a shared reverb or delay. The first time you turn one up, AMPLE writes a `send` block for that input, a shared `bus` and the reverb or delay (fully wet) that comes back on a free input of the mixer; the next strip reuses the same bus. The bus shows up as a strip of its own, with its own fader.

{{shot mixer-view-mobile | The mixer view on a phone | Tall faders and a scale on the output meter.}}

The blocks keep working as before: the mixer view is another way to reach the same parameters.

## Windows side by side

A block with an **Advanced** panel opens it in its own floating window. Open more than one and they stay side by side: drag a window by its header, and the song behind it stays usable. `Esc` closes the window you used last.

{{shot floating-windows | Two Advanced windows next to each other | A tone block and the reverb, both open at once.}}

Opening a panel builds its input for you when that is needed, with a **Cancel** button while it works, so you do not have to press Build first.

## The block library

Click the star on a block to put it under **Favourites**; the blocks you add most recently are listed under **Recent**.

Hold the pointer over a block (or focus it with the keyboard) to see a small preview: what it does, its kind and its first parameters. It shows after a short moment so it does not flicker while you move over the list, and `Esc` hides it.

{{shot library-hover | Hover a block in the library | A small preview shows what the block is and its first parameters; it waits a moment so it does not flicker, and Esc hides it.}}

{{shot library-favourites | The block library with favourites | A second click on the star takes the block away again.}}
