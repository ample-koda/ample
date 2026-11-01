---
title: Time signatures
order: 40
lead: How many beats are in a bar, and what kind of beat. It decides whether music marches or waltzes.
---
## Counting

A **time signature** is two numbers, for example 4/4. The top number says how many beats are in a bar. The bottom number says which note length counts as one beat (4 means a quarter note, see the next page).

- **4/4**: marching, pop, rock, dance. "1 2 3 4".
- **3/4**: waltz. "1 2 3, 1 2 3".
- **6/8**: rolling, two big beats each split in three. "1 2 3 4 5 6".

{{pv {"title":"4/4: strong tick, then three soft","src":"rhythm","p":{"meter":"4/4","bpm":120,"bars":2,"layers":[{"n":96,"at":[0]},{"n":84,"at":[1,2,3],"v":0.6}]}}}}
{{pv {"title":"3/4: strong tick, then two soft (a waltz)","src":"rhythm","p":{"meter":"3/4","bpm":90,"bars":2,"layers":[{"n":96,"at":[0]},{"n":84,"at":[1,2],"v":0.6}]}}}}
{{pv {"title":"6/8: six ticks with the strong ones on 1 and 4","src":"rhythm","p":{"meter":"6/8","bpm":90,"bars":2,"layers":[{"n":96,"at":[0]},{"n":91,"at":[1.5]},{"n":84,"at":[0.5,1,2,2.5],"v":0.6}]}}}}

## Feel it

Count out loud with the tick. When you can say "1 2 3" with the waltz and "1 2 3 4" with the march without losing the first beat, you have understood time signatures.

{{deeper Odd meters}}
Time signatures like 5/4 or 7/8 feel off-balance because the groups are uneven (for example 3+2). Famous examples are Take Five (5/4). Once 4/4 and 3/4 feel natural, uneven meters are a fun challenge.
{{/deeper}}

{{deeper Simple and compound meter}}
In **simple** meter each beat splits into **two** (4/4: "1 and 2 and 3 and 4 and"). In **compound** meter each beat splits into **three**. 6/8 is the usual example: two beats per bar, each divided in three (**1** 2 3 **4** 5 6). It feels rolling, a bit like a rocking boat. 9/8 has three beats of three, 12/8 has four.

The difference is easier to hear than to read: 3/4 sounds like "ONE two three", and 6/8 sounds like "ONE two three FOUR five six", with the sixes grouped in two lots of three.
{{/deeper}}

{{deeper The odd meters: 5/4 and 7/8}}
{{pv {"title":"5/4: strong on 1 and 4 (3+2)","src":"rhythm","p":{"meter":"5/4","bpm":150,"bars":2,"layers":[{"n":96,"at":[0]},{"n":91,"at":[3]},{"n":84,"at":[1,2,4],"v":0.6}]}}}}
{{pv {"title":"7/8: strong on the groups 2+2+3","src":"rhythm","p":{"meter":"7/8","bpm":105,"bars":2,"layers":[{"n":96,"at":[0]},{"n":91,"at":[1,2]},{"n":84,"at":[0.5,1.5,2.5,3],"v":0.6}]}}}}

Meters with an uneven grouping feel off-balance. 5/4 is usually 3+2 or 2+3. 7/8 is often 2+2+3. Counting the groups out loud helps: "ONE two three FOUR five" for 5/4. Famous examples are *Take Five* (5/4) and a lot of Balkan folk music (7/8). Once you can feel 4/4 and 3/4 well, these become a fun challenge rather than a mystery.
{{/deeper}}

{{ample Write the meter on the composition line: `composition "waltz" { ample 1  tempo 90  meter 3/4  master-seed 20261017 }`. A bar then has the beats of that meter (3/4 and 6/8: three quarter-note beats, 5/4: five, 7/8: three and a half), and notes use beat 0 up to the beats of the bar. The tempo always counts quarter notes. Open the code under any sound above to see it written out. The piano roll, the grids and the metronome follow the meter, and MIDI and MusicXML import and export use the meter of the file (the song that uses the imported module must say the same meter). Not yet: a meter cannot change inside a song, and a clip cannot have its own meter. | composition, midi-clip, /learn/ample/basic/bars-and-time=Bars and time in AMPLE}}