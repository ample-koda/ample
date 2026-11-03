# 16. Capabilities and the policy chain (design, with the first part being built)

**Status:** the effective-policy resolver, the settings syntax, the gating of the volatile built-ins and the diagnostics are built; network, filesystem, external-process and external-plugin are *reserved names*: the policy knows them, nothing in the language uses them yet (every future feature that touches the outside world must ask the policy first).

## 1. Why

AMPLE songs are programs. Part of the language reaches outside the song: the clock, OS randomness, the environment of the machine, and later the network, files, other processes and plugins. On a public playground or a shared server, the owner of the server must be able to say "none of that", and **no song, no AI proposal and no later line of the same song may take it back**. A capability is a permission, never a value.

## 2. The model

| Capability | Gates | Default | Notes |
| --- | --- | --- | --- |
| `clock` | `currentTime()`, `currentDate()` | on | volatile; a dynamic helper is still required |
| `os-random` | `osRandom()` | on | volatile |
| `environment` | `environment(name)` | **off** | sensitive: the environment holds secrets; volatile |
| `network` | (reserved) | off | |
| `filesystem` | (reserved) | off | |
| `external-process` | (reserved) | off | |
| `external-plugin` | (reserved) | off | |

Every capability has two independent bits at each level of the chain:

* **ceiling** (server only): may this capability ever be used here? `allow` or `forbid`. Nobody below can change it.
* **setting** (server default, project, song): `on`, `off` or unset (inherit).

### The chain and the monotone rule

```
server ceiling + server default
   ↓ (project settings, when the host provides them)
project
   ↓ (settings { capabilities.environment off } lines, in source order)
song
```

Walk the settings of the chain in order, starting from the server default `v`:

1. `off` at any level: `v = off` and the capability is **locked off** from here on.
2. `on` at any level: if the ceiling forbids it, **error** ("disabled by server policy"); if the capability is locked off, **error** ("turned off earlier by …: a capability cannot be turned on again"); otherwise `v = on`.
3. The **effective** value is `ceiling ∧ v`.

So a lower scope can only narrow: it can turn something off (for itself and everything below), it can turn on only what the scopes above allow and have not turned off. An explicit `off` in a song is a one-way door: a later `on` in the same song is a fatal configuration error (`AMP2076`); the capability stays off. `off` after `on` is fine (narrowing).

An unset capability that is off by default is **not locked**: the song may switch it on (when the ceiling allows). Only an explicit `off` locks.

### Security settings are monotone, resources are not

Capabilities (and any future sandbox switch) follow the monotone rule: once forbidden above, never allowed below. **Limits** (`limits.fuel`, `limits.stack-depth` ...) follow a different rule on purpose: a song may ask for *more or less* than the default, up to the **cap** of the server (doc 14, section 6); only the cap is a hard ceiling. A resource is a quantity to be negotiated, a capability is an authority.

## 3. Syntax

```
settings {
  capabilities.environment on
  capabilities.network off
}
```

`capabilities.<name> on|off`; names are checked (unknown name → `AMP2076` with did-you-mean). Several `settings` lines are read in order. A line the *host* injects above the song (a protected prologue) is read first, with the same semantics, but a host that needs enforcement must not rely on source text alone: it passes a **server policy** (ceiling and default) next to the song, because source text can be edited. Both are supported; the policy cannot be undone by any text.

**Allow-lists (built for `environment`):** `capabilities.environment.allow "TZ, AMPLE_*"` lists the names `environment()` may read (patterns: `NAME`, `PREFIX*`, `*SUFFIX`, `*PART*`, `*`). Every scope can add a list (server: `ample serve --environment-allow TZ,AMPLE_*`; project and song: the setting above) and a name must match **every** list, so a lower scope can only narrow, never widen. A name outside the lists is `capability-denied` at the call (`AMP2077`) with the lists named. Any other capability has no allow-list yet (`capabilities.network.allow` is the same idea for hosts, later).

**The project layer is built:** `ProjectSettings.Capabilities` in `.ample/project.json` (`{ "environment": "off", "environment.allow": "AMPLE_*" }`) is read when the project opens and sits between the server and the song for every parse.

## 4. Diagnostics and tooling

* Using a gated feature while it is off is an **error that says why**, never "unknown function": `AMP2077` "Capability `environment` is off by default: enable it with settings { capabilities.environment on }", "… is disabled by server policy", "… was permanently disabled by this song (line 3)". The same check runs statically on every helper body, so an unused helper with `environment()` is reported too.
* **Hover** on a gated name shows the capability, its effective state and the decisive scope.
* **Completion** keeps gated names but marks them: `environment (off: disabled by server policy)`, sorted last. Hiding them would make "why is it missing" a mystery; showing them with the reason is the better UX.
* The document exposes `Capabilities` (name, effective, deciding scope, reason) so the web editor, the LSP and Koda read one source.
* **Koda / AI proposals** may not turn a capability on or remove an `off` line, and may not raise a limit above the caps; the assistant text says so, and the proposal check refuses a draft that widens the policy (a proposal is untrusted input).
* A song whose policy denies a capability that one of its helpers needs is *not* silently degraded: the helper fails with `AMP2077` and the clip shows the error.

## 5. What the parser, the evaluators and the server do

| Layer | Change |
| --- | --- |
| Parser | reads `capabilities.*` settings in order (the settings dictionary alone loses the order), resolves the effective policy, reports `AMP2076` (configuration) and, per helper, `AMP2077` (denied use) |
| C# evaluator | `Context.Capabilities`; `currentTime`, `currentDate`, `osRandom`, `environment` check it and fail with the reason; a denied call never reads anything |
| JS twin | the same option, the same errors; `environment` reads an injected function (the browser has no environment: it returns "needs-server") |
| LSP / web | hover, completion detail and the signature note use `Document.Capabilities` |
| Server | passes the **server policy** (ceiling and default) into every parse; a public playground sets `forbid` for everything sensitive and ignores song text that tries otherwise; a future project policy file feeds the project level |
| Sandboxing | capabilities are the *language-level* gate; OS-level isolation (process limits, no network namespace) stays the host's job: the policy is a contract for the evaluator, not a replacement for a sandbox |

## 6. Open questions

* Pattern allow-lists for `network` hosts and `filesystem` roots (the `environment` one is built).
* A UI for the project layer (today it is the project file).
* Whether `clock` and `os-random` should be forbiddable on a deterministic-only server (they can: ceiling `forbid`); a song using them then fails with `AMP2077`, which is the intended "fully reproducible only" mode.
