---
title: Mixing blocks
order: 50
lead: Put the parts together: busses, sends, the mixer and the master.
---
## The idea

Every part goes through a few stages: its own effects, a level and a place, then a shared bus or mixer, and finally the master.

{{blocks mixer; bus; send; group; master; meter}}

- A `mixer` sums several sounds with a level for each.
- A `bus` collects many sounds to treat them together (for example all drums).
- A `send` sends a part of several sounds to one shared effect, such as one reverb for everything.
- A `group` keeps related blocks together.
- The `master` is the end of the chain. Put `limiter` and `normalize` before it.
- A `meter` shows the level so you can see what is going on.

{{try Share one reverb}}
Put a `send` after three instruments and connect the send output to a single `reverb`. Move the send level of each part: the louder the send, the further away that part sounds. One reverb, several depths.
{{/try}}
