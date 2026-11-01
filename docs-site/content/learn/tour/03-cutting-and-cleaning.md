---
title: Cutting, joining and cleaning
order: 30
lead: The editing blocks: cut a recording, join pieces, repair it and change its speed or direction.
---
## Cut and place

{{blocks trim; cut; join; crossfade; splitter; rewind; region-merge}}

`trim` removes the silence around a sound. `cut` keeps a stretch. `join` puts parts one after the other with a gap, and `crossfade` blends one into the next. `splitter` gives the left and right sides as separate sounds. `rewind` is the DJ pulling the record back. `region-merge` turns a region (a part of a sound you processed separately) back into audio.

## Change speed, pitch and direction

{{blocks stretch; pitch; shift; reverse; scratch; chop; granular}}

## Repair and clean

{{blocks denoise; deesser; normalize}}

Use `denoise` on a recording with steady hiss or room noise, `deesser` to soften harsh s sounds in a voice, and `normalize` to bring a very quiet recording to a sensible level without hunting for the right gain.

{{deeper Regions in one paragraph}}
Some blocks have a **region** output: a piece of the sound (for example the join of a loop) that you can process on its own. Connect effects to it, then bring it back to normal audio with `region-merge`. This lets you fix a click at a loop seam without touching the rest.
{{/deeper}}
