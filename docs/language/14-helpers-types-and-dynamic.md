# 14. Helpers, types, bounded control flow and dynamic code

**Status: built.** Helpers, types, effects (pure / dynamic), capabilities and limits, property expressions, `bake EXPR as VALUE` (all, per block, per call), the static checks of helper bodies (AMP2082 to AMP2087) and `# ample-ignore` are implemented in the language, the editors and the server (C# and the browser twin share the corpora `spec-tests/note-lang/core.json` and `static.json`); the diagnostics are in `08-diagnostics.md` and the changes in `CHANGELOG.md`. What is **not** built is named in section 13 (imports/export, lambdas, user records and generics, overloads, mutable globals, string manipulation and I/O, user-defined operators, cross-block property writes, reading audio values in expressions, transcendental functions) and in the text where it says so. The design reasoning below stays as it was written (2026-10-26). It builds on `12-note-pipelines.md` (pipelines, note bake) and `13-randomness.md` (seeds). The syntax below is what was designed; where the built language differs, `03-syntax.md`, `08-diagnostics.md` and the examples in `spec-tests/note-lang/` are the truth. Section 14 lists what the owner decided.

## 1. The shape of the proposal in one page

1. **One expression language, one function table.** A pipeline operator from the catalog (`transpose`, `shift`, `merge` ...) is a built-in helper whose first parameter is the receiver: `x |> f(a)` is exactly `f(x, a)`. User helpers go into the same table. Completion, hover, signature help, rename and the cost model therefore have one source, not two.
2. **A small bounded, resource-safe language.** `let`/`var`, `if`, `repeat`, `for`, bounded `while`, `return`, `emit` (only to build `Notes`). No lambdas, no overloads, no user-defined records, no user-defined generics (the built-in structured types `Note`, `List<T>` and `Range<T>` exist), no I/O, no mutable globals. Every potentially unbounded construct has a finite *effective* limit, set locally or inherited from the song, the project or the server; tooling shows it. Evaluation always ends: with a value or with a diagnosed failure (the language is *terminating*, not *total*: a limit being reached is a failure result).
3. **Strong types with inference.** Parameter types are written (or inferred from a default), local types are inferred, return types are inferred except in recursion. Unions are closed and declared. There is no `Any` and no `dynamic` type: *dynamic* is an **effect**, not a type.
4. **Two effect levels and a set of dependencies.** The *effect* is `pure` or `dynamic` (volatile: clock, OS randomness, environment) and propagates transitively (`pure + dynamic = dynamic`). It is always inferred from the body and the callees; the word `dynamic` on a helper is a **required permission**: a helper whose inferred effect is dynamic but that is not declared dynamic is an error (`AMP2060`, fix *Mark helper dynamic*). Seeded randomness is not an effect (it is reproducible): it is a *dependency*, next to `master-seed` (stable seeds), `tempo` (times in seconds), `clock`, `os-random`; dependencies decide what a bake must record, effects decide the stability of a song. A bake turns a dynamic value into a constant for everything downstream and keeps its origin (the expression, the volatile inputs it read) as provenance.
5. **One freeze concept, two spellings.** `bake EXPR as VALUE` freezes a value of any expression (call, subtree, property, helper); `|> bake name { ... }` stays the note stage of doc 12 (notes are big and structured: file sidecar, span, editable). Same idea, same states (fresh, stale, dirty, override), same menu.
6. **Properties may be expressions** (`gain helper(...)`). The plan id depends on the **resolved value**, never on the formula, so switching literal ↔ expression ↔ baked never rebuilds by itself. The UI never hides a formula and **the server refuses to overwrite one**: every editor (slider, knob, Llama, Jam) goes through one intent API with explicit modes.
7. **A fix is only automatic when it cannot change the music.** Rule of thumb: *if the resolved output hash stays the same, it may be applied by "Fix all"; otherwise the user chooses.*
8. **Tooling is the type checker.** `typeAt(position)` and `expectedTypeAt(position)` drive completion, hover, signature help and fixes. The parser must give a typed AST for incomplete code (`theme.notes |>` with nothing behind it).

My opinion on the biggest decisions is in section 13. The rest of this page works it out.

## 2. Names, scope and declaration collection

* Helpers are file-wide: **pass 1** collects every `helper`/`dynamic helper`/`type` declaration of the file into a *module table* (name, parameter list, declared effect, declared return type, source range); **pass 2** resolves every use; **pass 3** infers types and effects in call-graph order (strongly connected components; see 6).
* Declaration order never matters. A helper may sit at the bottom of the file.
* **Imports later without redesign:** the module table is keyed by `(module, name)`; today the only module is the file itself. A later `import "canon.amplet" { canonVoice }` adds a second module to the lookup; a helper hash is its normalized body plus the hashes of the callees and constants it actually references (transitively); the module is provenance only, so an unrelated change in the same module never makes other consumers stale. Declarations are private by default and a later `export` keyword costs nothing now.
* **Case:** names resolve ignoring case. Two declarations that differ only in case are an error. A use with other casing than the declaration resolves, with warning `AMP2051` ("Helper `CanonVoice` found, but casing differs") and the fix *Change to `CanonVoice`* (safe, S0). Built-ins resolve case-insensitively and are rewritten to their canonical casing by the formatter.
* **Spelling convention:** AMPLE names are kebab-case (`random-seed()`, `canonVoice`), types and conversions are TitleCase (`Int`, `Round`). The volatile built-ins are camelCase (owner decision): `currentTime()`, `currentDate()`, `osRandom()`, `environment()`. Hyphens in identifiers need one lexer rule: a hyphen belongs to an identifier only between a letter and a letter or digit, `i-1` is `i`, `-`, `1`, and `a-b` is one name (lint AMP2057 asks for spaces around subtraction).
* Helper names, block names and stable names are different namespaces (a call has parentheses, a block reference has a dot, a stable name is a string), so a helper and a block may share a name; a lint warns because it reads badly.

## 3. Types

| Type | Notes |
| --- | --- |
| `Int`, `Float`, `Bool`, `String` | `Number = Int \| Float` is built in. Float is IEEE-754 double, only `+ - * /`, comparison and the defined conversions (no `sin`/`exp`: they differ between C# and JS); a transcendental set can come later as deterministic software implementations. |
| `Length`, `Time`, `Duration` | `Length` = bars/beats/ticks (the musical domain, stored in beats), `Time` = s/ms (the absolute domain, stored in seconds): two dimensions of their own. Converting between them needs the tempo; the *recipe* records the tempo exactly when a time in seconds is converted into the beat domain of notes (as in doc 12), not otherwise, `Duration = Length \| Time` (what `Duration delay` in the brief means). Exact rational micro-beat maths, never floats. |
| `Gain` (dB), `Percent`, `Pitch`, `Velocity` | Quantity types with units; `Percent * Number` is `Percent`, `Number * Gain` is `Gain`. |
| `Notes`, `Note` | `Note` has fields `at`, `len`, `pitch`, `vel` (read-only record, built-in). |
| `Seed`, `StableSeed`, `RandomStream`, `RandomSource = Seed \| StableSeed \| RandomStream` | From doc 13. |
| `List<T>`, `Range<T>` | Built-in generic containers; users cannot declare generics (v1). |

* **Inference:** `var delay = 500ms` is `Time`; `var amount = 0.25` is `Float`; `var seed = stable("verse")` is `StableSeed`. Ambiguity (`[]`, a bare `choose()` of mixed types) is an error with the fix *insert explicit type*.
* **Unions:** declared with `type Name = A | B` (file-local, no generics). **No `dynamic`-as-escape-hatch:** a union value only offers operations valid for *every* member; the others need narrowing with `if x is Int { ... }` (the branch sees `Int`). Completion shows the common operations, and lists the member-specific ones greyed out with "needs `is` check".
* **Numeric conversions:** `Int → Float` implicit and lossless. `Float → Int` is implicit only when the value is whole (`4.0`); any other decimal is an **error** `AMP2052` (rounding is a musical choice, so it is not made silently), with the fix `Round(x)` and the other choices (`Floor`, `Ceil`, `Truncate`) in the hint, all S2. `Int(x)` and `Float(x)` are explicit and fail (`AMP2053`) when not exact; the two rules now agree.
* **Overloads: none in v1.** Named and default arguments cover the use. Built-in operators may have union-typed parameters (for example `shift(Duration)`), which keeps completion and rename unambiguous.
* **Recursion needs a declared return type**, everything else is inferred.

## 4. Helpers

```
helper canonVoice(Notes source, Number interval, Duration delay) -> Notes {
  source |> transpose(interval) |> shift(delay)
}
```

* `helper name(params) -> Type { body }`; `dynamic helper` marks the volatile effect; parameters may have defaults (`Int voices = 4`), and a call may use named arguments (`buildCanon(theme.notes, voices=3, interval=+7, delay=2bars)`).
* The body is a block. The value is the last expression or `return x`. `var` is a mutable local that never escapes the helper; `let` is immutable. (The brief uses `var` everywhere: I recommend `let` as the default, and `var` only where a loop accumulates.)
* **`emit`** is allowed only in a helper that returns `Notes`: each `emit x` merges `x` into an implicit accumulator (exactly `merge`: the span is the longest, nothing is de-duplicated, the canonical text sorts by position and pitch, and only notes at the same position and pitch keep their emit order; the accumulator's recipe is order-sensitive, so emit order can only cause a conservative re-bake, never a different sound). A bare `return` ends the helper early with what was emitted; `return value` together with `emit` is an error (`AMP2054`) because the helper would have two candidate results. Emit of anything but notes is a type error.
* **Seeds and helpers:** `stable("hat")` inside a helper is the same namespace on every call, so two calls would draw the same humanization. That is correct but surprising, so lint `AMP2055` says "this stable name is shared by N calls" with the fix *make the seed a parameter* (S2) and the idiom `seed.fork("voice", i)` inside loops.
* **Cost summary per helper:** every helper gets an inferred interval of fuel and events, computed per call-graph component; hover shows it (section 11).

## 5. Bounded control flow

```
repeat 8 { emit pattern.notes |> shift(index * 1bar) }
for i in 0..=7 { emit theme.notes |> transpose(i * 2) |> shift(i * 1bar) }
for note in source { ... }
while condition max 128 { ... }
```

* `repeat N` and `for i in a..b` need a bound that is a constant or an `Int` expression; `..` is half-open and `..=` inclusive exactly as in doc 12 (so the brief's `0..7` is seven iterations, `0..=7` eight; a lint hints when the loop variable is later used as a count).
* `for x in <finite>`: ranges, `List<T>`, `Notes` (each `Note`). Never over an unbounded value.
* **`while cond max N`**: the guard is always present. Without `max` the effective **while-default** is used and shown (hover and an inline hint `max 256 (default)`); fix *Add explicit `max 256`* is S1 (output preserving). **Reaching the max is an error by default** (`AMP2056`: silently truncated output is a change of meaning), with the loop's partial result kept for inspection; `max 128 stop` says "stopping here is intended". The latter spelling is a proposal.
* **Static cost:** a loop multiplies the cost of its body by its upper bound (the constant bound, else the effective limit); nested loops multiply; the estimate is shown live and warns above 50% of the effective limit (extends AMP2037).
* Prefer operators: `for note in notes` with `emit` is O(events) interpreted; lint `AMP2058` suggests the operator when one exists (`|> transpose`).

## 6. Recursion, fuel and limits

* Recursion is allowed. Safety is **explicit-stack evaluation** (no native recursion in C# or JS) with **stack-depth**, **fuel** and **event/memory caps**, so a runaway helper ends with a clean diagnostic, never a crashed process.
* Calls form a graph; strongly connected components are found once. A recursive component with no visible base case (no `if ... return` before the self call) gets lint `AMP2059`. Tail calls are not optimized (v1).
* **Fuel** is an abstract, deterministic unit (one per evaluated node, N per operator step, events weighted). Because it is abstract, C# and JS run out at the same instruction, so "fuel exhausted" is a reproducible language result. **Wall time is not deterministic**: it only ever produces an *environment failure* ("took too long on this machine"), never part of a take's identity.
* Hierarchy: **server cap** (fixed, configuration only) > **project** policy (default) > **song** (`settings { limits { ... } }`, may raise or lower, never above the server cap, a raise is shown with a badge) > **local** annotation on a helper or loop (`@limits(stack-depth 8)`, may only tighten). `@limits { }` at song level from the brief is accepted as sugar for the `settings` block so there is one place.

| Limit | Meaning | Proposed default / server cap (to be measured) | User-settable |
| --- | --- | --- | --- |
| `loop-iterations` | iterations of one loop | 10,000 / 1,000,000 | yes |
| `while-default` | max when `while` has none | 256 / 10,000 | yes |
| `stack-depth` | helper call depth | 64 / 256 | yes |
| `fuel` | total evaluation budget | 2,000,000 / 100,000,000 | yes |
| `events` | notes in any value (existing `notes.maxEvents`) | 200,000 / 2,000,000 | yes |
| `memory` | built (AMP5713): the notes made by one evaluation together, estimated at 64 bytes per note, counted at every pipeline result and helper result (both evaluators count the same) | 64 MB / 1,024 MB | yes (`settings { limits.memory 256 }`) |
| `bake-time` | wall clock of one evaluation | 20 s / 60 s | yes (a machine matter, not part of identity) |
| `list-length`, `string-length` | container sizes | 100,000 / 1,000,000 | no (internal) |
| parse nesting, token count, trace buffer, cancellation poll interval, fuel weights | engine internals | fixed | no |

## 7. Dynamic (volatile) code and stability

* **Volatile built-ins (a deliberately small, closed set):** `currentTime()`, `currentDate()`, `osRandom()`, `environment(name)`. Each has effect `dynamic` and needs a **capability** (`clock`, `os-random`, `environment`; page 16); user code cannot create a new source of volatility except by calling these, and a denied capability is an error that says why.
* **Effect inference:** `effect(helper) = join(effects of its body and callees)`. `dynamic helper` declares the top; a helper *not* marked dynamic whose inferred effect is dynamic gets `AMP2060` with fixes *Mark helper dynamic* (S1: output identical, status changes) and *Bake the dependency* (S2). Values carry taint: `var m = currentTime().minute` is dynamic, and so is everything computed from `m`.
* **Reachability decides the song status**, not mere existence: a dynamic helper nobody uses does not make the song dynamic.

| Song status | Meaning |
| --- | --- |
| **Stable** | nothing dynamic reaches any block property or note source |
| **Dynamic** | at least one unbaked dynamic dependency is reachable; the song may preview and build |
| **Baked Dynamic** | every reachable dynamic root is baked and fresh; reproducible |
| **Dirty/Stale** | a bake no longer matches (stale recipe or edited by hand) |

* **Finals** require *Stable* or *Baked Dynamic* with everything fresh; a Dynamic song can build and preview, but cannot be marked as a reproducible final.
* **Every take of a dynamic song records its dynamic inputs** (the resolved value of each volatile call) in its provenance. The song is not reproducible, the take is, and the "Always" rule gets *stronger*, not weaker.

## 8. Bake scopes: one construct

I collapse the brief's four scopes into **two spellings of one idea**:

| Scope in the brief | Spelling here | Notes |
| --- | --- | --- |
| call-site, inline, expression/subtree | `bake EXPR as VALUE` | `EXPR` is whatever follows `bake`: a call, or a parenthesized subtree `bake (a + b) as 7`. Same syntax at every position. |
| helper-level | `dynamic helper f() -> Int bake as 5 { ... }` | Only for helpers **without parameters** (a parameterized helper has no single value). For others the action *Bake helper* means "bake each call site" (preview, multi-edit) or, better, *Bake the dynamic dependency inside* (`bake currentTime() as ...` in the body). |
| call site | `phase()` becomes `bake phase() as 3`, `hourShift(3)` becomes `bake hourShift(3) as 15` | Built as the code action "Bake this call" (LSP), the button **Bake this call** in the source editor (cursor on the name of the call) and the edit op `bakeCallSite`; the helper itself stays dynamic. The arguments must be plain values (numbers, words, constants, other calls): a call that gives notes or reads other blocks is not a value; bake its notes in Jam instead. |
| the whole song, after a build | the button **Bake clock values** (bar above the blocks, next to Build) and the edit op `bakeDynamicHelpers` | Bakes every clock-dependent value with the clock of the **last build** of that song (the server remembers the instant): the dynamic helpers without parameters get ` bake as VALUE` in their declaration (the body stays), a direct clock read in a block (`transpose(currentTime().second % 4)`) becomes `bake currentTime().second % 4 as 3` where it stands, and a call of a dynamic helper with parameters becomes `bake hourShift(3) as 15`. `osRandom()` and `environment()` are skipped (not repeatable). One undo step through the normal edit path, journaled on save. |
| one block | right-click a block, Edit, **Bake clock values of this block** (and the same op with `block`) | Only that block's expressions, and calls of helpers without parameters are baked at the call, so the helper stays dynamic for the other blocks. |
| into a new song | the dialog of both buttons: **In a new song** (route `POST /api/doc/bake-clock-as`) | Writes the baked text to a new song file of the project and opens it; the open song stays as it is, still dynamic: it is the seed of any number of baked songs. |
| note/output materialization | `\|> bake name { ... }` | unchanged from doc 12 |
| property | `gain bake helperGain(theme.notes) as -4dB` | same `bake EXPR as VALUE` after the property name |

* **Disambiguation:** `bake` right after `|>` is the note stage; `bake` at the start of an expression is a value bake and needs `as`.
* **The expression stays in the source**, so provenance is the source: *Make dynamic again* removes `bake` and `as VALUE`; *Show original expression* is just the text; *Re-bake* replaces `VALUE` by a fresh evaluation.
* **Signature (recipe hash) only for non-volatile EXPR.** `bake currentTime().minute as 5` is volatile: any value is as good as another, no staleness exists. `bake helperGain(theme.notes) as -4dB @a1b2c3d4` records the Merkle recipe hash `@...` (normalized expression, helper hashes, inputs, operator versions, master seed when `stable(` is used, tempo when time units are used); if the recipe hash changes it is **stale**.
* **Override is explicit:** `gain bake helperGain(theme.notes) as -2dB @a1b2c3d4 override`. Rules: recipe unchanged and value equals the evaluation = **baked**; recipe unchanged, value different, no `override` = **dirty** (somebody typed over the number: fixes *Accept as override* / *Re-bake*); with `override` = **override**, shown as "Baked −2 dB, source now resolves to −4 dB". Replacing by a literal removes the whole construct.
* `override` makes three different intents three different texts: **bake** (freeze what the expression gave), **override** (a different value on purpose, expression and provenance kept), **literal** (the binding is gone).

## 9. Block properties as expressions

* **Which properties:** each template parameter has a declared type (templates already declare them). An expression is allowed where the type matches **and the property is not structural**: `at`, `for`, `from` links, block names, `parts` stay literal (or constant-foldable without block reads) because layout and graph shape must not depend on evaluation (they would create cycles with `for auto` and the dependency graph).
* **Read:** `block.prop` reads the *resolved* value (after that property's own expression, with defaults); typed from the template. **Write:** only a block's own property lines; there are no cross-block writes (`echo.gain = 3` is not a thing): a property has exactly one place where its value is declared.
* **Phases:** a value is *Static* (constants and pure helpers), *Notes* (needs resolved notes, available at plan time without audio) or *Build* (needs a rendered take, e.g. loudness). Property expressions may use Static and Notes values; reading Build values is unsupported in v1 (the plan id would depend on the take being planned: a cycle by construction).
* **One dependency graph** over properties, note sources (`block.notes`) and helpers. Cycles are errors showing the chain: `gain → helperGain → theme.notes → ... → gain`.
* **Two identities.** The *build* identity (plan id) is content-addressed: it uses the resolved values, so equal values never rebuild, and when a value depends on the tempo or the master-seed and one of them changes, the resolved value changes and the plan id follows by itself. The *bake* identity (the recipe hash of a value or note bake) is dependency-addressed: it includes the normalized expression, the used helpers and the dependencies (master-seed, tempo) that actually took part. Two expressions with the same value but different dependencies get the same plan id and different recipes, which is exactly what staleness needs. Whitespace and comments never count; renaming a helper counts (it edits the expression), and a re-bake that gives the same value cuts the cascade off.
* **Plan id uses resolved values.** `gain -4dB` and `gain helperGain(theme.notes)` that resolves to −4 dB give the *same plan id*; converting between them never rebuilds. A dynamic property changes the plan id per build, and the take records the value.
* **Property states** (all visible in Properties and in the block): *literal*, *expression-backed*, *baked*, *override*, *readonly*, *stale/dirty*.

## 10. Editors: controls, Jam, Llama

### 10.1 One binding API, and a server guard

A property binding is `{ state, resolved, expr, source, bakedValue, recipeHash }`. The web exposes `getBinding(block, prop)` and `setValueIntent(block, prop, value, mode)` with `mode ∈ override | bake-and-set | literal | edit-expression | cancel`. The edit operation (`setPropertyValue`) **refuses a plain overwrite of an expression-backed property** and answers with a conflict that names the modes. So no UI, including specialised ones, can clobber a formula by accident, and no editor needs its own rule.

### 10.2 Touching a control

On first movement of a slider or knob on an expression-backed property the control keeps dragging **provisionally** (audio preview only, source untouched) and a small non-modal chip appears:

```
gain ƒ  helperGain(theme.notes)  now −4 dB
[Override with −2 dB]  [Bake −4 dB and edit]  [Replace with literal]  [Edit expression]
```

Release commits with the highlighted default **Override** (the least destructive: the expression stays in the source); Esc restores. Nothing is remembered silently.

### 10.3 Specialised controls (Llama and the rest)

Custom block UIs (delay-llama: `vowel`, `voice`, `mouth`, `vibrato`, `gain`) read `getBinding` for each field, render the *resolved* value with an `ƒ`/lock badge and hover (source, provenance), and write only through `setValueIntent`. A field that is expression-backed shows the value but its drag handle uses the chip of 10.2.

### 10.4 Jam

* **Literal/editable mode:** as today.
* **Derived mode** (pipeline or helper result): resolved notes read-only, coloured by origin; `editable { }` pieces are editable and written back by span; a dynamic result shows a banner "resolved at 14:20, will change" with *Bake resulting notes*.
* **Scratch preview:** an in-memory working copy for audition and tweaking, never touching the source. It ends with *Keep as editable literal* (bake and detach), *Keep as baked stage* or *Discard*. No silent replacement, ever.

## 11. IntelliSense, hover, diagnostics, fixes

**Engine:** the C# type checker (in `Ample.Language`, served by the LSP) provides `typeAt`, `expectedTypeAt`, `scopeAt`, resolved values (by evaluation, on demand and cached by recipe hash) and provenance traces (tracing is opt-in because it costs). Incomplete expressions yield `Unknown`, which suppresses cascading errors and widens the candidate list instead of emptying it.

* **After `|>`:** the left type `T` selects every function whose first parameter accepts `T` (subtyping, unions, implicit numeric widening), ranked by relevance; `Notes |>` offers note operators and `Notes` helpers, `500ms |>` offers `Time` operations.
* **Arguments:** signature help with parameter name, type, default, return type, effect (pure/seeded/dynamic), docs, union members and the bake/dynamic implication; named-argument completion (`interval=`), and `expectedTypeAt` filters values (a `Duration` slot offers duration variables and unit literals).
* **Properties:** completion knows the type and the units a property accepts; `seed=` offers the stable namespaces of the file, forks and streams (doc 13).
* **Snippets:** `helper`, `dynamic helper`, `for`, `repeat`, `while ... max`, `bake ... as`, `type`.
* **Navigation:** go to definition (helpers, locals, properties, blocks), find usages, hover references, rename helper / local / property reference / block. **Renaming a stable name changes the derived seeds and therefore the music**: it is an S2 action with an impact list (the same wording as the copy dialog) and never part of a bulk rename.
* **Semantic tokens** (subtle, modifiers rather than colours per category): `dynamic` (italic + warm accent), `baked` (lock glyph in the gutter, dotted underline on `as VALUE`), `stale/dirty` (amber wavy underline), `readonly`, `seeded`/`stream` (teal), expression-backed property (small `ƒ` before the value and tinted key). Pure code stays plain.
* **Hover** composes only the parts that exist: inferred type, resolved value, source expression, signature, effect and bake state, freshness, effective limits, estimated cost (`≤ 12,000 events, ≤ 40k fuel`), provenance (call chain, seed path `stable("verse").fork("b")`), union narrowing info, the warning and its fixes.
* **Diagnostics:** exact range, severity and text on hover, fix buttons in the tooltip (the web already receives fixes), lightbulb in the gutter.

### Fix safety classes

| Class | Rule | Applied by "Fix all" |
| --- | --- | --- |
| **S0** | purely syntactic, cannot change any resolved value (casing, formatting, spacing) | yes |
| **S1** | output hash provably unchanged but source structure changes (insert explicit type, add `max` equal to the effective default, `Round(...)` when it equals the current implicit rounding, mark helper dynamic) | yes, listed in a preview |
| **S2** | may change the music or the status of the song (did-you-mean to another name, Floor/Ceil/Truncate, tighten a limit, narrow/cast a union, Re-bake, Detach, Accept/re-sign, Override, Replace with literal, Bake dynamic dependency, rename of a stable name) | never; always one explicit choice with a before/after hint |

The verifier is mechanical: a fix declares `preserves: true` and the tool *proves it* by comparing resolved output hashes before and after in a dry run; if the proof fails the fix is downgraded to S2.

| Situation | Fix(es) | Class |
| --- | --- | --- |
| wrong helper casing | Change to `CanonVoice` | S0 |
| unknown helper, near match | Did you mean `canonVoice`? | S2 (S0 if only the casing differs) |
| lossy Float → Int | `Round(...)` / `Floor` / `Ceil` / `Truncate` (an error, no choice is made for you) | S2 |
| ambiguous type | Insert explicit type | S1 |
| invalid union use | Narrow with `is` / Cast | S2 |
| pure helper uses a dynamic dependency | Mark helper dynamic / Bake dependency | S1 / S2 |
| `while` without `max` | Add `max <effective default>` | S1 |
| estimate above limit | Insert / tighten limit | S2 |
| invalid unit or context | Convert (`500ms` → `~0.2bars` at this tempo, says so) | S2 |
| invalid operator after `\|>` | Replace with a valid operator (shows the list for the type) | S2 |
| stale bake | Re-bake / Detach / Use live expression | S2 |
| dirty bake | Accept as override / Re-bake | S2 |
| expression-backed property touched | Override / Bake and edit / Replace with literal / Edit expression | S2 |
| formula-backed Jam content | Bake resulting notes / Make editable | S2 |
| helper/property cycle | shows the chain; Go to source | none |
| readonly baked value edited | Unlock and detach / Override | S2 |

Codes proposed: AMP2044–AMP2070 (types, helpers, loops, effects, bake-as, property states), runtime AMP5710+ (fuel, stack, loop guard, dynamic taint), to be allocated in `spec/diagnostics.json` with invalid-corpus cases.

## 12. Evaluation, parity, hashing, safety

* **Evaluators:** the existing decision stays (C# authoritative, JS live) but the helper language is far bigger than the operator set. My recommendation: **phase 1 evaluates helper code only in C#** and the browser asks the resolver endpoint (as it already does for non-literal notes); the language core gets a **JS twin once semantics settled**, driven by the same golden corpus (programs → values, hashes, fuel counts), which makes the twin mechanical. The corpus is written with the language, not after.
* **Hashing:** values have a canonical type-tagged encoding (SHA-256); helper hash = normalized body + the hashes of the callees and constants it references (component-sorted for recursion), no module hash; recipe hash is Merkle as in doc 12; early cut-off stays: a re-bake that gives equal output keeps downstream hashes.
* **Atomicity and cancellation:** every evaluation writes into a temporary result and commits through one `CommandBus` write; progress events carry the current helper/stage and fuel/event counts; a Stop button is always visible; cancel or failure leaves the previous state untouched. OOM is avoided by design (static estimate, fuel, event and memory caps, stack depth, lazy streaming of notes, no unbounded containers).

## 13. My opinion, tradeoffs, and what stays out of v1

* **Right:** one function table, effects instead of a `dynamic` type, resolved-value plan ids, explicit `override`, a server-side guard on property writes, and the mechanical "hash unchanged ⇒ safe" rule for fixes.
* **I would change from the brief:** `let` over `var` as default; `currentTime()` kebab-case; `bake EXPR as V` as the single value-bake spelling (call-site, inline and subtree are the same thing); helper-level bake only for parameterless helpers; hitting a `while` max is an error unless you say `stop`; `emit` only for `Notes`; no `Float → Int` silently except exact values; `0..7` is half-open like the rest of AMPLE.
* **Biggest risks:** (1) scope creep into a general language (stay total, no lambdas); (2) JS/C# divergence (corpus first, JS later); (3) fix authors weakening the safety rule (the mechanical proof guards it); (4) helper-local stable names being shared (lint plus parameter idiom); (5) hyphenated names versus subtraction (lexer rule, lint).
* **Unsupported on purpose in v1:** imports/export (designed for, not built), lambdas and first-class helpers, user records and generics, overloads, mutable globals, string manipulation and I/O, user-defined operators, cross-block property writes, reading build-phase (audio) values in expressions, transcendental float functions, recursion without declared return type, `at`/`for` as block-reading expressions.

## 14. Design decisions

1. **`let` is the default**; `var` only for mutable locals.
2. Reaching `while ... max` is an **error** (`max N stop` says it is intended).
3. The volatile built-ins are **camelCase**: `currentTime()`, `currentDate()`, `osRandom()`, `environment(name)`.
4. The JS twin of the helper language is built **directly after** the C# evaluator, before the editor work, and checked by the same golden corpus.
5. The limit defaults are a starting point and a safety margin for a single user; they are measured later.
6. `Duration` stays `Length | Time` (page 15 explains the tempo dependence).
7. `override` is an explicit keyword.

The musical vocabulary (pitch classes, modes, intervals, chords) is page 15.

## 15. Phased plan

| Phase | Content | Done when |
| --- | --- | --- |
| H0 | This page, grammar, `types.json`, diagnostics allocation, golden corpus of programs (C#) | spec lint green, corpus written before code |
| H1 | Module table, declaration collection, type inference, effects, call graph; `let`/`var`/`if`/`return`; C# evaluator with fuel and explicit stack; `helper` in `notes` and property expressions (pure only) | helpers work in pipelines, hash/cost stable, tests green |
| H2 | `repeat`/`for`/`while max`, limits hierarchy and `settings { limits }`, cancellable evaluation, progress | loops safe under the absurd-input corpus |
| H3 | Dynamic effect and taint, volatile built-ins, `bake EXPR as V`, helper `baked`, `override`, stability status, take provenance | the stability states are visible and Finals respects them |
| H4 | Properties as expressions: binding API, `setPropertyValue` server guard, Properties panel states, chip on controls, Llama adapter | no editor can overwrite a formula |
| H5 | Jam derived/scratch modes with helpers and dynamic banner | scratch keeps/discards without touching source |
| H6 | IntelliSense: typed completion, signature help, hover, semantic tokens, fixes with the mechanical safety proof, navigation and rename | each example in section 16 works as shown |
| H7 | JS twin of the language core against the same corpus (if decided), Koda text and guide pages | corpus identical in both |

## 16. Examples (`.amplet` and UI sketches)

The first block is one small song; later examples show the variations. The syntax of the helpers, types, `bake` and `# ample-ignore` in these examples is built; the user-interface parts are sketches.

```
# 1. simple utility helper, 2. typed musical helper
helper canonVoice(Notes source, Number interval, Duration delay) -> Notes {
  source |> transpose(interval) |> shift(delay)
}
midi-clip second at 1 for auto {
  notes canonVoice(theme.notes, +7, 2bars)
}

# 3. a helper using another helper (declared later in the file: order does not matter)
helper buildCanon(Notes theme, Int voices = 3, Number interval = +7, Duration delay = 2bars) -> Notes {
  for i in 1..=voices {
    emit canonVoice(theme, interval * i, delay * i)
  }
}

# 4. inferred typing, 5. explicit typing
var delay = 500ms              # Time
var amount = 0.25              # Float
var seed = stable("verse")     # StableSeed
Notes theme2 = theme.notes
Duration pause = 2bars

# 6. union and numeric conversion
type Steps = Int | Float
helper delayBy(Notes n, Steps s) -> Notes {
  if s is Int { return n |> shift(s * 1beat) }
  return n |> shift(Round(s) * 1beat)     # Float narrowed away: explicit and intended
}

# 7. for
helper stack(Notes source) -> Notes {
  for i in 0..=7 { emit source |> transpose(i * 2) |> shift(i * 1bar) }
}

# 8. bounded/defaulted while (hover: "max 256 (default)")
helper grow(Notes n) -> Notes {
  var result = n
  while count(result) < 40 max 64 { result = result |> merge(n |> shift(1bar)) }
  return result
}

# 9. recursion with a declared return type, guarded by stack-depth
helper echo(Notes n, Int times) -> Notes {
  if times == 0 { return n }
  return n |> merge(echo(n |> shift(1beat) |> transpose(-12), times - 1))
}

# 10. pure versus dynamic
helper double(Int x) -> Int { return x * 2 }
dynamic helper currentVariation() -> Int { return currentTime().minute % 8 }

# 11. call-site bake (the helper stays dynamic), 13. subtree bake
var a = bake currentVariation() as 2
var b = bake (currentVariation() + 1) as 6

# 12. helper-level baked value (parameterless helpers only)
dynamic helper repeatCount() -> Int bake as 5 {
  if currentTime().second % 2 == 0 { return 5 }
  return 4
}

# 14. dynamic -> pipeline -> normal note bake
midi-clip variation at 5 for auto {
  notes theme.notes
    |> transpose(currentVariation())
    |> bake chosen-version { input-hash 77aa..  notes-hash 12bc..  span 4bars  notes 1:0:1:62:100 ... }
}

# 15. property driven by a helper, 16. property reading a property, 17. baked, 18. override
gm lead at 1 for 8 {
  gain helperGain(theme.notes)                       # ƒ  now -4 dB
  mix  chorusEnergy() * 20%
}
reverb tail {
  enabled echo.enabled
  gain bake helperGain(theme.notes) as -4dB @a1b2c3d4                    # baked
}
reverb tail2 {
  gain bake helperGain(theme.notes) as -2dB @a1b2c3d4 override           # source now: -4 dB
}
```

```
# 31. the canon demo rebuilt with helpers and control flow
composition "canon" { ample 1  tempo 100 master-seed 424242 }

helper voice(Notes theme, Int n, Number interval, Duration delay) -> Notes {
  theme |> transpose(interval * n) |> shift(delay * n)
}
helper buildCanon(Notes theme, Int voices = 4, Number interval = +7, Duration delay = 2bars) -> Notes {
  for n in 0..voices { emit voice(theme, n, interval, delay) }
}
midi-clip theme at 1 for 4 { notes "1:0:1:60:100 1:1:1:62:100 1:2:1:64:100 1:3:1:65:100" }
midi-clip canon at 1 for auto { notes buildCanon(theme.notes, voices=4) |> humanize(timing=20ms, seed=stable("canon")) }
```

### UI sketches

```
19. Slider on an expression-backed property (non-modal chip, drag is provisional)
  gain ƒ ──●────── −4.0 dB                helperGain(theme.notes)
  ┌ [Override with −2.0 dB] [Bake −4 dB and edit] [Replace with literal] [Edit expression] ┐

17/18. Properties panel
  Gain   −4 dB   ƒ  baked              Source: helperGain(theme.notes)        [Re-bake] [Make dynamic again]
  Gain   −2 dB   ƒ  override           Source now resolves to −4 dB            [Use source result] [Keep override] [Replace with literal]
  Mix    12 %    ƒ  expression         chorusEnergy() * 20%                   [Bake current value] [Edit expression] [Go to helper]

20. Jam literal mode:   editable roll, all notes editable (as today).
21. Jam derived mode:   read-only roll coloured by origin; editable { } pieces editable;
                        banner (dynamic): "Resolved at 14:20, will change   [Bake resulting notes] [Make editable]"
22. Jam scratch:        [Scratch copy of 'canon']  ▶ audition  [Keep as editable literal] [Keep as baked stage] [Discard]

23. Llama (delay-llama):  vowel ● a e i o u   (ƒ badge, hover: "random(...) via stable("vowel")")
                          touching the dial shows the chip of 19; the source is only written after a choice.

24. IntelliSense after  theme.notes |>     (type: Notes)
     transpose(Number)   shift(Duration)   bars(Range)   merge(Notes)   humanize(...)   canonVoice(Number, Duration)  [helper, pure]
25. Signature help      canonVoice(source: Notes, interval: Number, delay: Duration = 2bars) -> Notes   · pure
                                                       ▲ Number = Int | Float   named: interval=
26. Hover on  delay      var delay: Time = 500ms
                         resolved 500 ms   ·  expression: bake currentVariation() as 5 (baked dynamic, no staleness)
                         provenance: stable("theme").fork("variation", 5)    effective limits: loop 10,000 · fuel 2M
                         cost ≤ 220 events    ⚠ none   quick fixes: Re-bake · Make dynamic again · Go to helper
27. Fix casing    canonvoice(...)  ⚠ Helper `CanonVoice` found, but casing differs.   💡 Change to `CanonVoice`   (S0)
28. Fix lossy     interval * 0.5 → Int slot  ⚠ Float to Int loses 0.5   💡 Round(...)  Floor(...)  Ceil(...)  Truncate(...)
29. Fix taint     helper nudge(...)  uses currentTime()  ⚠ not marked dynamic   💡 Mark helper dynamic · Bake dependency
30. Fix stale     |> bake old {…}  ⚠ stale: its recipe changed   💡 Re-bake · Detach · Use live expression
```


## 17. Design decisions and the naming rules

Decided after a critical read of the draft; each point says what was kept and why.

1. **"Total" is the wrong word.** With bounded loops, recursion guarded by stack depth and fuel, and limits that end an evaluation with an error, the language *terminates* (always a value or a diagnosed failure), but it is not *total* in the technical sense. The word is now "bounded, resource-safe".
2. **Effects.** The effect axis is `pure`/`dynamic` only. Seeded randomness is reproducible, so it is a dependency (with `master-seed`, `tempo`, `clock`, `os-random`), not an effect. `seed=7` and `stable("x")` both "use randomness"; only the second depends on the master-seed. Dependencies are inferred transitively per helper and exposed to tooling (`HelperInfo.Dependencies`). Effects are always inferred; the declaration is a required permission (point 6).
3. **Length / Time.** Two dimensions, stored in their own base unit (beats and seconds); conversion needs the tempo and the recipe records the tempo when, and only when, a time in seconds is converted. The earlier wording ("Length is tempo independent") was too simple.
4. **Float to Int is an error** (page section 3). There was no compatibility reason for the implicit rounding; it contradicted "a fix never changes the music silently".
5. **Identity.** Build identity is content-addressed (resolved values), bake identity is dependency-addressed (recipe). Not one or the other: they answer different questions (section 8 and the two-identities note).
6. **Dynamic helpers are explicit.** A helper whose body (or callee) is dynamic and that is not declared `dynamic` is an error with the fix *Mark helper dynamic*. `dynamic` over-declaration (declared dynamic, nothing dynamic inside) is allowed: it is an upper-bound assertion, useful while a helper is being written. The status of a song never trusts the declaration: it uses the inferred effect.
7. **Names.** Helpers, functions and built-ins are `lowerCamelCase` (`canonVoice`, `randomSeed`, `currentTime`); types, conversions and constructors are `TitleCase` (`Notes`, `Int`, `Round`, `Note`); blocks and resources keep their kebab-case. A kebab-case helper name is a lint (`AMP2076` family: *helper-naming*, fix: the camelCase spelling); `random-seed()` stays as a deprecated alias of `randomSeed()` with a warning.
8. **Hyphens in expressions.** In an expression, `a-b` is never silently one name and never silently a subtraction. Resolution: if only the symbol `a-b` exists (a block, a constant ...) it is that symbol; if only `a` and `b` exist it is an error with the fix `a - b`; if both exist it is ambiguous (`AMP2074`) with the fixes `a - b` and `[a-b]`; if neither exists it is unknown. `[name-with-hyphens]` is the escaped symbol (it is also how a block with a hyphen is referenced when ambiguity exists: `[lead-main].notes`). It collides with the list literal only for a one-element list of an unspaced subtraction: `[a-b]` is always the symbol; write `[a - b]` or `[a-b,]` for the list. Name positions (block headers, `from` lines) keep working without brackets.
9. **Records and generics.** Wording fixed: no *user-defined* records or generics; `Note`, `List<T>`, `Range<T>` are built in.
10. **`emit` and `return`.** Kept the rule (a bare `return` is the early exit); the merge semantics are written down in section 4. `build { }` was a leftover of an earlier draft and is not part of the language.
11. **Module hashing.** Per-helper semantic hash plus the hashes of what it references; the module is provenance only (section 2).
12. **`environment()`** stays, behind the capability model of page 16 (off by default, forbiddable by the server, never re-enabled below a forbidden scope).
13. **Limits.** Reworded: every potentially unbounded construct has a finite effective limit; the effective limit and where it comes from are part of the document (`Document.Limits`) and shown in hover.
14. **Two concrete problems** (`pickRegister` returning an `Octave` used as a GM program; a helper called `stretch-by` that shifts) were real: the first is now a type error (values keep the musical type they were made as), the second was a name; it is `delayBy`.


## Conditions: true-ish values, `!`, `&&`, `||` and `cond ? a : b` (E1)

A condition (`if`, `while`, `!`, `&&`, `||` and the condition of `?:`) reads a value with one rule, **truthiness**:

| Value | Counts as true when |
| --- | --- |
| Bool | it is `true` |
| Int, Float | it is not 0 |
| a quantity (a length, a time, an interval, a gain, a percent) | its number is not 0 (`0ms` and `0%` are false) |
| String, a symbol (a bare word) | it is not empty |
| List | it has items |
| Range | it is not empty (`3..3` is empty, `3..=3` is not) |
| Notes | it has at least one note |
| a single Note, a seed, a random stream, a time of day | always true |

`!a` is `not truthy(a)`. `a && b` and `a || b` give **true or false** (never one of the operands), and stop at the first operand that decides (the other side is not worked out). `c ? x : y` works out only the branch that is taken; the two branches may have different types (the type of the expression is the union, the hover shows it), it is right associative (`a ? b : c ? d : e`) and a branch is a whole pipeline (`a ? b : c |> d` pipes the second branch). Write parentheses to pipe the whole conditional: `(a ? b : c) |> d`. A condition that is not a Bool is allowed (that is the point of the rule); to be strict write the comparison (`n != 0`, `len(list) > 0`).
