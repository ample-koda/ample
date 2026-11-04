# Dictionary of AMPLE

The words of AMPLE and of music, in plain language. How to add a word: see `guide/README.md`.
Format: `## word`, optional `aliases:` and `see:` lines, then the meaning in Markdown. The first sentence is what the tooltip shows.

## bar
aliases: bars, measure, measures
see: beat, tempo
A bar (also called a measure) is one small chunk of a song, the unit that music is counted in. In AMPLE almost every bar has **four beats**, so a bar lasts as long as four beats at the current tempo.

At 120 beats per minute a beat takes half a second, so a bar takes two seconds. At 60 it takes four. The song text says where things happen in bars: `at 1 for 8` means "start at bar 1 and last 8 bars".

The first bar of the song is bar 1 (not bar 0). Bar 5 starts right after bar 4 ends; "5.5" would be half way through bar 5.

## beat
aliases: beats
see: bar, tempo
A beat is one pulse of the music, the thing you tap your foot to. Four beats make a bar. Drum patterns divide each bar in 16 smaller steps, so one beat is 4 steps.

## tempo
aliases: bpm
see: beat, bar
The tempo is how fast the song goes, counted in **BPM** (beats per minute). 60 BPM is one beat a second; 120 BPM is two. It is written on the project line: `composition "my song" { ample 1  tempo 100 }`.

## song
aliases: songs
see: project, block
A song is one text file with the extension `.amplet`. It says which blocks exist, how they are connected and how the project is set. Everything you see in the node editor is drawn from that text, and the other way round.

## project
aliases: projects
see: song
A project is a folder that holds your songs and everything they use: samples, MIDI files, lyrics, notes, and a hidden `.ample` folder with the history and the built audio.

## block
aliases: blocks
see: port, connection
A block is one part of the song that makes or changes sound: a chord clip, a drum machine, a bass, a mixer, an effect. In the node editor a block is a box; in the text it is a paragraph with curly braces `{ }`.

## port
aliases: ports
see: connection, block
A port is a dot on the side of a block where a cable can be attached. Inputs are on the left, outputs on the right. A port has a type, such as **audio** (sound) or **notes** (which notes to play), and only matching types can be joined.

## connection
aliases: connections, cable, cables
see: port, block
A connection joins an output port of one block to an input port of another. In the text it is a line inside the receiving block: `from chords.notes -> notes` means "take the notes output of the block chords and feed it into my notes input".

## node editor
aliases: node view
see: block, source view
The node editor is the main picture of AMPLE: blocks as boxes on a canvas with cables between them. The **Source** view shows the same song as text; you can work in either.

## build
aliases: builds, building, built
see: take, render, cache
Building is making the sound of the song from its blocks. AMPLE only builds what is **out of date**: a block that did not change, and does not depend on one that did, is not made again. A build never plays anything; press **Play** for that (it builds first when it must).

## render
aliases: renders, rendering, rendered
see: build, take
To render is to calculate the actual audio of a block. A build renders the blocks that need it; "render to file" writes the result of a block as a WAV or MP3 file.

## take
aliases: takes
see: build, cache, plan
A take is the audio a block made in a build, stored in the project. If nothing about a block changed, the next build finds its take and does not render again. When a block changes, its old take stays stored, and the block gets a new one.

## cache
aliases: cached, cache hit
see: take, build
The cache is the pile of takes that were already made. A "cache hit" means a block was found there and did not have to be rendered again, which is why a second build of an unchanged song is almost instant.

## out of date
aliases: stale
see: build, take
A block is out of date when something it depends on changed since its last build: its own text, a file it reads (such as a sample), or a block that feeds it. Out of date blocks have no take and show "not built". The **Play** button says "Build and play" when something is out of date.

## plan
aliases: plans
see: take, seed
A plan is the complete recipe of a render: the text of the block, the files it reads, the seed, the version of the engine. The take is stored under the fingerprint of its plan, which is how AMPLE knows that the same plan gives the same sound.

## seed
aliases: seeds
see: master seed, take
A seed is a number that steers the "random" choices of a block, such as slight timing differences or which of several recorded drum hits is picked. The same seed gives the same result every time; another seed gives another variation of the same music.

## master seed
see: seed, project
The master seed is the number on the project line (`master-seed 424242`). Blocks that have no seed of their own derive theirs from it, so changing the master seed changes the variation of every such block at once.

## sample
aliases: samples
see: sample set, drums
A sample is a short recording, such as one drum hit, stored as a WAV file in the project. Sampled instruments play recordings instead of making the sound from scratch.

## sample set
aliases: sample folder, kit
see: sample, drums
A sample set is a folder of samples that belong together, for example a drum kit (`kick.wav`, `snare.wav`, `hat.wav`) or the recorded notes of a guitar. A block points at the folder, and swapping the folder swaps the sound.

## MIDI
see: note
MIDI is a way to write down music as instructions (which note, how long, how hard) instead of as sound. A MIDI block tells an instrument what to play; the instrument turns that into audio.

## note
aliases: notes
see: MIDI, chord
A note is one tone with a pitch (how high), a start, a length and a force. Blocks pass notes to each other through ports of the type **notes**.

## chord
aliases: chords
see: note
A chord is several notes played together, such as **Am** (A minor) or **F**. The chord block takes names like `Am F C G` and turns them into notes, one chord per bar.

## drums
see: pattern, step
The drums block plays a drum pattern. With a sample set it plays recorded drum hits; without one it uses a built-in synthesised kit.

## pattern
aliases: patterns
see: step, bar
A pattern is a short loop of notes or hits that repeats, for example one bar of kick and snare. A drum pattern is written as a row of steps for each drum voice.

## step
aliases: steps
see: pattern, bar
A step is one position in a drum pattern. A bar has 16 steps, so each step is a sixteenth note long. A step either has a hit (`x`) or is empty (`.`).

## velocity
see: note
Velocity is how hard a note is played, from soft to loud. On a drum step, `x` is a normal hit, `X` an accent and `o` a quiet ghost note.

## volume
aliases: gain, dB
see: pan
Volume (or gain) is how loud something is, measured in **dB** (decibels). 0 dB changes nothing; every -6 dB halves the strength of the signal (it sounds clearly quieter, though not literally half as loud) and every +6 dB doubles it. Each block and each mixer input has a level.

## pan
aliases: panning
see: volume
Pan puts a sound on the left or right in the stereo picture. 0 is the centre; negative values go left and positive values go right.

## mixer
see: master, volume
The mixer adds several sounds together into one, with a level and pan for each. Cables from your instruments go into its inputs; its output goes on to the master.

## master
see: mixer, mix
The master is the last block of the song: what comes out of it is the song you hear and export. The block called `master` is the one the **Play** button plays.

## mix
aliases: mixing
see: mixer, master
The mix is the whole song with all parts at their right levels. Making the mix means setting the levels and pans so every part can be heard.

## effect
aliases: effects
see: block, reverb
An effect changes a sound that goes through it: a reverb adds a room, an EQ shapes the tone, a compressor evens out the loudness. An effect block sits between an instrument and the mixer.

## reverb
see: effect
Reverb adds the sound of a room around a sound, the echo you hear in a hall or a bathroom. The **mix** setting says how much of it you hear.

## loop
aliases: loops, looping
see: pattern, bar
A loop is something that repeats. A loop can be a pattern that repeats each bar, an audio recording that repeats, or the **Loop** switch in the header, which repeats the audio you are listening to.

## section
aliases: sections
see: bar, pattern
A section is a named stretch of a song, such as verse or chorus. In AMPLE, sections can name a part of a block (`at 1 for 8 as verse`) and drum patterns can be strung together per section.

## lane
aliases: lanes
see: port, mixer
A lane is an extra output of the sequencer block that carries only some of its channels (for example only the drums). You can connect a lane to any block, so the sequencer feeds several places.

## revision
aliases: revisions
see: line, checkpoint
A revision is a saved state of the project at one moment. Every save adds one. The history is the list of revisions, and you can go back to any of them.

## line
aliases: lines of work
see: revision, studio, garage
A line is a chain of revisions, like a path through the history. A new project has two: **studio** and **garage**. You can start more.

## checkpoint
aliases: checkpoints
see: revision
A checkpoint is a snapshot of the saved files that you take yourself, with a name, to be able to come back to exactly this moment. It changes nothing else.

## studio
see: garage, promote
Studio is the main version of the project: the one you would call "the real song". It only changes when you promote something to it.

## garage
see: studio, promote
Garage is where you work: every save lands on the garage line. Try things there freely; the studio stays as it was until you decide otherwise.

## promote
aliases: make this the main version
see: studio, garage
To promote is to make the studio the same as the garage. In the History panel the button says **Make this the main version**. The old studio stays in the history, so nothing is lost.

## fork
aliases: new try, start a new try
see: line, studio
To fork is to start a new line of work from the main version, as a new garage. The button in the History panel says **Start a new try**. What you had on the old garage stays in the history.

## source view
aliases: source editor, code editor
see: song, node editor
The source view shows the song as text, the way it is saved, with a red underline under lines that have a problem. Everything you can do in the node editor you can also do here by typing, and the two always show the same song.

## diagnostic
aliases: diagnostics, error, errors
see: build
A diagnostic is a message about a problem in the song text, shown in the Source view. Build errors are the ones that appear when a block cannot be made; they have a code such as AMP5380 and underline the line they point at in red.

## fade
aliases: fades, fade in, fade out, fadeout, fadein
see: crossfade, volume, bar
A fade changes the volume over time: a fade in grows from silence, a fade out shrinks to silence. The start and the length are counted in bars or in seconds, and a fade out ends in exact silence.

## crossfade
aliases: crossfades
see: fade
A crossfade blends one sound into another: while the first fades out the second fades in, so a change from one groove to the next has no gap. Use the **equal** shape so that the middle does not sound quieter.

## sequencer
see: channel, playlist, lane
The sequencer is a block that holds a small studio: channels, patterns and a playlist, in a file of the project. It has a full-screen workspace in the style of loop-based music programs.

## channel
aliases: channels
see: sequencer, pattern
A channel is one sound of a sequencer: a drum with its sample, a repeating loop, a bass or a melody. Every channel has its own name, colour, level and pan.

## playlist
see: sequencer, pattern, section
The playlist of a sequencer says when each pattern plays: you put clips of patterns on tracks along the bars, and make the clip longer to repeat the pattern.

## piano roll
see: note, MIDI
The piano roll is a grid on which you draw notes with the mouse: time runs from left to right and pitch goes up like the keys of a piano. Every change you make is written to the notes text of the clip.

## bundle
aliases: bundles, library bundle
see: slot, take, seed
A bundle is a block or a chain of blocks saved in your library with its text, its seeds, the files it reads and the audio it made, so you can place it in another song and hear the same sound.

## slot
aliases: slots
see: bundle, sample set
A slot is a setting of a bundle that names a file or a sample folder. You can point a slot at another file when you place the bundle, and that block is then built again with it.

## refactor
aliases: refactoring
see: project panel
To refactor is to change something and fix everything that depends on it in one step. When you rename a file in the project panel, AMPLE updates all the lines of your songs that name it, and shows them first.

## project panel
see: project, sample
The project panel is the Project tab at the bottom of the window: the files of your project as a tree and as tiles, where you add, rename, drag and use them.

## General MIDI
aliases: GM, sound font, soundfont
see: MIDI, note
General MIDI is a standard list of 128 instrument sounds, such as piano, bass and strings, that every compatible instrument knows by number. A sound font is the file with the actual sounds for those instruments.

## pitch
see: note, MIDI
Pitch is how high or low a note sounds. MIDI gives every semitone a number: 60 is middle C, 61 is the next key up, 59 the one below.

## EQ
aliases: equalizer
see: effect, filter
An EQ (equalizer) turns the low, middle or high part of a sound up or down, like the bass and treble knobs of a stereo.

## filter
aliases: filters, lowpass, low-pass, highpass
see: EQ, effect
A filter removes part of a sound: a lowpass lets the lows through and cuts the highs, so the sound gets duller; a highpass does the opposite. The cutoff says where the cut starts.

## delay
see: effect, reverb
A delay repeats a sound after a short time, like an echo. The feedback says how many times it comes back, and the mix how loud the repeats are.

## compressor
aliases: compression
see: effect, volume
A compressor makes loud parts quieter, so the difference between loud and soft gets smaller and the whole sound can be turned up.

## arrangement
aliases: timeline
see: bar, block
The arrangement is the Arrangement tab: the whole song on a timeline, with the bars on a ruler and a row for every block that shows when it plays.

## log
aliases: build log
see: build, diagnostic
The log is the Log tab: the lines of the last builds, such as which blocks were made, which came from the cache and which failed, with the reason for each failure.

## read-only
aliases: look only
see: revision, diagnostic
Read-only means you can look but not change. AMPLE is read-only while the song has errors that must be fixed in the source first, and while you look at an old revision.

## sidechain
aliases: sidechain compressor, duck, ducking
see: compressor, kick
A sidechain compressor listens to another sound (its key) instead of to its own input: whenever the key is loud, it presses its own sound down. With the kick as the key, a pad or a choir gets out of the way of every kick and comes back between the hits.

## humanize
aliases: midi-humanize, humanizing
see: velocity, note
To humanize is to make music played by a machine sound played by people: every note is moved a tiny bit in time and in velocity, differently every time. Two voices humanized differently never sound exactly together, which is what makes one sound into a crowd.

## voicing
aliases: voicings
see: chord, note
A voicing is the way a chord is spread over notes: which notes, in which order and which octave. The same chord, Am for instance, can be a few close notes or a wide spread. Close voicings that move by small steps sound smooth.

## kick
aliases: kick drum
see: drums, sidechain
The kick is the big drum of a drum kit, the low thump on the beat. It carries the rhythm, so it is often used as the key of a sidechain compressor.

## package
aliases: packages, ampletx
see: bundle, take
A package is one file (`.ampletx`) that holds a saved song and the files it needs, so you can open it on another computer. A Full package can be built again from scratch; a Derived package replaces the first part of the song by audio.

## proposal
aliases: proposals
see: package
A proposal is a change that the AI assistant made in a temporary copy of your files. Nothing in your project changes until you accept it, and you can reject it.
