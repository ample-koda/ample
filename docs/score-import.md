# Score import: from a MIDI, MusicXML, MuseScore/Guitar Pro or PDF/image score to a sequence

> Status (2026-10-15): built (tool `ample_import_score`). For the assistant and for people.

A score that exists as **MIDI** (`.mid`) or **MusicXML** (`.musicxml`, `.xml`, or zipped as `.mxl`) becomes a sequence file that AMPLE can play and edit, without a conversion script. A **MuseScore, Guitar Pro, Capella or Overture** file (`.mscz`, `.mscx`, `.gp`, `.gp3`-`.gp5`, `.gpx`, `.ptb`, `.ove`, `.cap`, `.capx`, `.bww`) is converted to MusicXML with [MuseScore](https://musescore.org) when it is installed - see "MuseScore/Guitar Pro/Capella/Overture files" below. A score that only exists as a **PDF or image** (`.pdf`, `.png`, `.jpg`, `.jpeg`, `.webp`, `.tif`, `.tiff`) is recognized first with [Audiveris](https://audiveris.org) (optical music recognition, OMR) when it is installed - see "PDF and image scores (OMR)" below.

## What you get
`ample_import_score { path, out?, bars_per_pattern? }` writes a new `.ampleseq` in the proposal:
- one **channel** per part: `channel violin1 gm file=pack:musescore-general program=40 bank=0 vol=-6 pan=0` (the General MIDI program of the part is kept; a drum channel uses bank 128);
- the notes as rows `bar:beat:length:pitch:velocity` in **patterns of `bars_per_pattern` bars** (default 8), named `part-1`, `part-9`, ...;
- a **playlist** that plays the patterns in order (`clip part-1 track=1 at=1 for=8`).
The answer says how many parts, notes and bars there are, the **tempo** of the score (set the project tempo to it) and **warnings** for what could not be kept.

## A MIDI file in pieces, with the sound of the file (2026-10-28)
The import of a MIDI file keeps what the MIDI renderer plays, in pieces of `bars_per_pattern` bars:
- **Exact ticks:** the channel line has `ppq=480` (the ticks of the file) and the rows are `bar:tick:length-in-ticks:pitch:velocity`; a note that goes over a bar line is cut there and its pieces share `;tie=N`, so it plays as one note. A short drum gate keeps its real length.
- **Controllers and pitch bends:** a row `<channel>.events` in each pattern holds them as `bar:tick:c|p:data1:data2` (c = controller such as 7 volume, 11 expression, 64 sustain, 10 pan; p = pitch bend with its two seven-bit bytes). The soundfont renderer plays them with the notes.
- **Tempo changes:** the notes and events are put at their real time at the first tempo (`ppq` grows to the unit that makes every tick a whole number, for example 960 when a part runs at twice the first tempo), so the sound is right and the bars of the sequence are those of the first tempo.
- **Level:** a channel is imported with `vol=-6` (thirteen parts at 0 dB clip, the MIDI renderer has a soft limiter that the sequencer has not). Set `vol=0` when you want the level of the MIDI block.

Checked on a 13-part MIDI (2026-10-28): the sequence and the `midi-render` of the same file are equal to 1 unit of 16 bits in the stretch without tempo changes (the sequencer is stereo, so a centred channel is 3 dB lower on one side: the pan law); where the tempo changes, both start every note at the same moment, the sequence to the sample and the MIDI renderer to the tick (1/480 beat), so the waveforms differ in phase and not in energy.

**Short.** A pattern that says exactly what an earlier one says is not written again (the playlist plays the earlier one) and a pattern that shares most rows with an earlier one is a variation (`from=`) that lists only what differs. With `compress=true` a row is written as a formula (a rhythm that repeats with pitches and velocities that cycle, `docs/language/17-note-completeness-and-formulas.md`) when that is at least 40 percent shorter and was checked to give the very same notes.

## What it keeps and what it does not
| Kept | Not kept (a warning says so) |
| --- | --- |
| Parts and their program, pitches, start and length in beats, ties (joined), chords, rests, simple repeats (a backward repeat jumps back once) | Time signatures other than 4/4 (a bar here is four beats), tempo changes (the first tempo is used), MIDI controllers that the soundfont renderer does not play (all controllers are kept in the events row, the renderer plays 7, 11, 10, 64 and the pitch bend), first and second endings (played in written order), dynamics (all notes at velocity 90 for MusicXML, the written velocity for MIDI), grace notes, ornaments and articulation |

## How to use it in a song
1. Put the score file in the project (or fetch it with `ample_fetch_url`; Mutopia and similar sites offer MIDI and MusicXML).
2. `ample_import_score { path: "scores/k525.mid", out: "sequences/k525.ampleseq" }`.
3. Add a sequencer block that names the file and give it lanes for the sounds you want to treat separately:
   ```
   sequencer score {
     file sequences/k525.ampleseq
     lanes lane1=violin1+violin2 lane2=viola lane3=cello+bass
   }
   ```
4. Set the project tempo to the tempo from the answer. A song is as long as the playlist; a part may reach bar 1024.
5. Change instruments, volumes and lanes in the sequence; make it sound the way the song needs (see `sequencer`).

## MuseScore/Guitar Pro/Capella/Overture files
When `path` ends in one of MuseScore's own read formats, AMPLE runs [MuseScore](https://musescore.org) (`<file> -o <file>.musicxml`) and imports the result - this is a straight, machine conversion of the file's own note data (not recognition), so it is as reliable as the source file:
- **Install MuseScore** (not bundled with AMPLE): the official installer works; a portable build (its `.paf.exe` accepts `/S` for a silent self-extraction next to itself, or use the PortableApps.com build) needs no admin rights. Set `AMPLE_MUSESCORE` to the resulting `MuseScore4.exe`, or put it on the PATH. `ample doctor --env` reports whether it is found.
- A `.mxl` (zipped MusicXML) needs neither Audiveris nor MuseScore: AMPLE unzips it directly.

## PDF and image scores (OMR)
When `path` ends in `.pdf`, `.png`, `.jpg`, `.jpeg`, `.webp`, `.tif` or `.tiff`, AMPLE runs [Audiveris](https://audiveris.org) on it in batch mode and imports the MusicXML it recognizes:
- **Install Audiveris** (not bundled with AMPLE): the easy way is `ample packs install audiveris`, which the Windows installer (a feature "Audiveris", off by default) and the AMPLE Tune-up wizard also offer. It downloads the pinned upstream release (5.11.0, about 80 MB) from GitHub, checks its SHA-256 and unpacks it for you alone into `<AMPLE home>/audiveris` (no administrator rights; `dpkg-deb` on Ubuntu 22.04 and 24.04, an administrative extraction of the MSI on Windows); AMPLE finds it there by itself. `AMPLE_AUDIVERIS` still wins over it. By hand: its Windows installer needs admin rights for a normal install; without them, extract it per-user with `msiexec /a Audiveris-x.y.z-windowsConsole-x86_64.msi /quiet TARGETDIR="<a short path>"` (a long `TARGETDIR` can fail with "Error writing to file" - Audiveris's own class files have long names). Set `AMPLE_AUDIVERIS` to the resulting `Audiveris.exe`, or put it on the PATH. `ample doctor --env` reports whether it is found.
- **It can be wrong.** OMR reads printed symbols, not music: a scanned or low-quality source, unusual notation, or a lot of text can come out with missing, extra or mis-pitched notes. The answer's `next` note says to check the result; always look at the note count and a few bars against the source before trusting it.
- **Getting a PDF into the project first:** a score fetched from the web lands outside `./project` (`ample_fetch_url`'s `assets/web/`); stage it into the project with `ample_copy_file` (`source: "workspace/assets/web/<file>"`) before importing it.
- Large or dense scores can take a few minutes; Audiveris is stopped after 3 minutes with a timeout message if it has not finished.

## Tips
- Long scores: more `bars_per_pattern` makes fewer, longer patterns; fewer makes it easier to rearrange sections.
- The import never overwrites a file: give another `out` or change the file with `ample_edit`.
