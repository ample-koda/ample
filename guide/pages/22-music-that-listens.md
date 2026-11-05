---
title: Music that listens
summary: Let a sound steer the music: a car that passes opens a filter, a hit plays for every car.
---

# Music that listens

A **control** is a number that changes over time and that you use to steer a parameter. It can come from a sound (how loud it is), from a curve you write, or from another control. This page builds the example of the Lite demo "Cars passing the music": a street with rain and a passing car answers the music.

## Listen to a sound: follow

```
follow traffic stable@1.0 {
  band low
  baseline on
  attack 100 ms
  release 900 ms
  from street.mix -> in
}
```

`follow` listens to the sound that comes in and makes a control: high while it is loud, low while it is quiet.

- **band** says which part you follow: `low` (the engines), `mid`, `high`, `full`, or your own range such as `80 Hz .. 400 Hz`.
- **baseline on** subtracts the slow average, so steady rain does not count and only what stands out of it (a car) does.
- **attack** and **release** say how fast the control rises and falls.
- `auto` in `band`, `attack`, `release` or `baseline_window` lets AMPLE choose from the recording. `ample follow-suggest recording.wav` prints the lines that write the choice down.

The control has two outputs: `level` and `gate` (on while the sound is above the `threshold`).

## Use it: two ways

**Written in the parameter**, next to `auto(...)`:

```
reverb space stable@1.0 {
  mix 10 + 60 * traffic.level
  from opens.out -> in
}
```

The mix is 10 percent in the rain and up to 70 percent while a car passes. Numbers with a unit, `+ - *`, brackets and `block.output` are allowed.

**Connected**, like a sound:

```
filter opens stable@1.0 {
  from swell.level -> cutoff
}
```

Every parameter that can change over the song has a control input of its own with the name of the parameter.

## Units

A control without a unit is a **level from 0 to 1**; it is scaled to the range of the parameter it steers. A control with a unit (`control<Hz>`, `control<dB>`) only steers a parameter in that unit. A `map` block converts: `unit Hz`, `out_low 300`, `out_high 4200` makes a cutoff from a level, and it can invert, limit and smooth.

## More blocks

- `curve` writes a control as a curve (`auto(1:0, 5:1000)`), so one curve can steer several parameters.
- `fire` plays a note every time a control passes a level, with a minimum time between two notes: one hit for every car.

## In a component

In the graph you can also drag a control line from the dot of a control block: every parameter that can be automated then shows its control input, and you drop the line on it. A component can hand out a control next to its sound (and `curve` and `fire` can be stages in its process): `output traffic: Control` and in the process `emit audio -> audio` (the sound as it is) and `emit audio |> follow(band="low") -> traffic`.

## See it

After a build, a control has a lane in the arrangement with its curve, and in the graph its links have their own colour.
