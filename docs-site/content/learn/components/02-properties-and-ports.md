---
title: Properties and ports
order: 20
lead: The contract of a component: what goes in, what you can set, what comes out.
---
## Ports

`input name: Type` and `output name: Type`. In this version the ports carry notes, and a component has one output. An input may be `optional`, with a `fallback` property that stands in when nothing is connected. Inside `process`, `connected(source)` says whether an optional input has a connection.

## Properties

`property name: Type = default`. The types are the musical types of the language (such as Int, Float, Percent, Duration, Gain, Interval and Notes, and lists and ranges of them); components have no types of their own. A default is required except for notes, and it must be a value of its type and inside its range. A property of the type Notes can be edited with Jam.

## Private properties

`private property name: Type = value` is a value of the component that only its own process reads. It is not a setting of an instance: it is not in the property panel or the library, and an instance cannot set it. Use it for a number the component needs but the user should not change. The value is required, may be a calculation from the inputs, the public properties and the private properties above it, and is worked out for each instance. `private` is a rule about who may change it, not a secret: anyone who opens the component can read it. Audio components cannot have one yet.

```ample
#[MinValue(1), MaxValue(8), Step(1)]
property echoes: Int = 3

private property spacing: Duration = 1beat
```

## Attributes

An attribute stands on its own line above a component, a port or a property: `#[Name(args)]`. Only the built-in ones exist. To every tool that does not know attributes the line is a comment.

| Attribute | On | Meaning |
|---|---|---|
| `MinValue(n)`, `MaxValue(n)`, `Step(n)` | a number-like property | the range and the step of the editor; an instance outside the range is an error |
| `Options("a", "b")` | a text property | the allowed values, shown as a choice |
| `DisplayName("..")`, `Description("..")` | anything | the name and the text shown to people |
| `Category("..")` | the component | the group in the palette |
| `Tags("a", "b")` | the component | words the library search also finds |
| `Experimental` | the component | may still change in ways its version does not say |

Prefer a type to an attribute when the type already says it: write `Percent` instead of a number with a unit attribute.

## Process

`process { ... }` is the body of a helper: the inputs and properties are its parameters, in the order they are declared, and `emit`, a `return` or the last expression gives the output. A process that uses the clock, OS randomness or the environment must say `requires clock` and the policy must allow it.

{{ample A process is written like a helper, with pipelines and bounded loops. | /learn/ample/language/expressions=Expressions}}
