> Status (2026-10-02): design sketch from a brainstorm. Nothing here is built or tested.

# Jargon (glossary for AMPLE)

AMPLE uses real studio and music terms. `ample explain "<term>"` explains them and plays an example.

## Score (dynamics, tempo, articulation)
| Term | Meaning |
|---|---|
| pp p mp mf f ff | very soft ... very loud (pianissimo ... fortissimo) |
| crescendo / diminuendo | gradually louder / softer |
| ritardando / a tempo | slow down / back to tempo |
| staccato / legato | short and detached / smoothly connected |
| fermata | hold the note longer |
| tacet | this part is silent |
| tutti | everyone plays |
| da capo / coda | back to the start / closing section |

## Song structure
intro, verse, pre-chorus, chorus, bridge, break, turnaround, vamp, tag (repeat at the end), outro.

## Directing the band
| Term | Meaning |
|---|---|
| lay back | play slightly behind the beat |
| push | play slightly ahead of the beat |
| in the pocket | tight, grooving on the beat |
| ghost notes | very soft in-between notes |
| fill | short flourish, usually on drums |
| stop-time | only accents, the rest silent |
| half-time | the groove feels half as fast |
| build / drop | build up to / suddenly release |

## Studio (behind the glass)
| Term | Meaning |
|---|---|
| take | one recording attempt, numbered and kept |
| comp | assemble the best parts of several takes |
| punch in / out | re-record only a short section |
| overdub | record on top of what exists |
| double / stack | layer the same part several times |
| bus / send | shared channel / route to an effect |
| duck | automatically quieter while something else plays |
| pan | left-right placement |
| bounce / stem | render out / a separate group of tracks as audio |
| tighten | pull the timing in line with another part |

Alias idea (tolerant parser): `lay back` = `layback` = `behind the beat` (Dutch: `achter de tel`). Files always store the canonical form.
