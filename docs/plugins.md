> Status: protocol version 1 is implemented and marked **experimental**; the ACE-Step plugin is the first real plugin that uses it.

# Plugins (as built)

A plugin is a program in its own folder with a `plugin.json`. AMPLE starts it as a separate process, talks to it over stdin and stdout and never lets it write outside its job folder. Everything that is not built in (AI voices, analysers, effects) arrives this way.

## The manifest (`plugin.json`)
| Field | Meaning |
| --- | --- |
| `id`, `version`, `title` | Lower-case id with dashes, dotted version, display name. |
| `kind` | `voice`, `analysis` or `effect`. |
| `protocolVersion` | `1`. A newer major is refused (`AMP5271`). |
| `experimental` | True until the protocol is frozen. |
| `entry` | `command` and `args` of the program; arguments are relative to the plugin folder. |
| `platforms` | `windows`, `linux`, `macos`. |
| `blocks` | The block types it renders (for example `ai-voice`). |
| `determinism` | `exact`, `machine-exact` (same bytes on the same machine and toolprint) or `unverified`. |
| `toolprintFields` | What the plugin puts in its toolprint (engine commit, model hashes, hardware class). |
| `needs` | `gpu` (the host runs one GPU job at a time), `network`, `diskMb`, `secrets` (names; only these are passed, as environment variables). |
| `settings.schema` | A small JSON Schema (type, enum, minimum, maximum, pattern, required, default, additionalProperties) that drives the settings editor and validates the values (`AMP5279`). |

All problems of a manifest are reported at once (`AMP5270`). `ample plugins validate <folder>` checks one.

## The protocol (version 1)
JSON-RPC 2.0, **one JSON message per line** on stdin (host to plugin) and stdout (plugin to host). Stderr is captured as log text.
- `initialize {protocol, host}` returns `{protocol, id, version, blocks}`; the protocol must be 1.
- `describe {}` returns `{toolprint, determinism}`; the toolprint becomes part of every plan.
- `validate {block, settings}` returns `{ok, messages}`.
- `render {jobDir, block:{type,name,parameters}, seed, sampleRate, samplesPerBar, firstBar, barCount, activeBars, inputs, settings}` returns `{outputs:[{role, path}], meta}`. Large data goes through files: inputs are in `jobDir/in/`, outputs must be written inside `jobDir` and are named by relative path.
- `analyze {jobDir, analysis, input: "in/audio.wav", options}` returns `{result}` and may return bounded `{outputs:[{role, path}]}` artifacts for file-producing analysis jobs. Analysis output paths must be relative to the private job folder and point to files the plugin wrote there.
- `cancel {id}` and `shutdown` are notifications; `progress {id, fraction}` and `log {level, message}` flow from the plugin.

## Local audio transcription

## Local reference voice synthesis

Quoted voice parameters are decoded as language strings, including an empty `reference_text`. With `auto_detect on`, an empty transcript invokes Whisper rather than being passed to Qwen as literal quotes. Plugin 0.1.5 invalidates takes made with the former decoding behavior.

Each engine can have its own interpreter: `voice-clone.pythonXtts`, `voice-clone.pythonQwen3` and `voice-clone.pythonChatterbox` (empty means not set). The one for the block's `model` wins over `voice-clone.python`, so an environment that only works for one engine is used only for that engine. An explicit `voice-clone.python` is authoritative for every engine: dependency checks report missing modules or incompatible versions instead of changing interpreters or automatically installing into that environment. Progress shows `sys.executable`. Qwen with `auto_detect on` also requires `faster_whisper`; disable auto detection when supplying the transcript manually, or prepare that dependency in the selected environment. The generic `python` default retains automatic setup behavior. Plugin 0.1.4 invalidates previous takes so a former CPU-fallback result is not silently reused.

Dependency compatibility note (verified 2026-10-10): qwen-tts 0.1.1 requires Transformers 4.57.3; a shared environment with Transformers 5.2.0 failed to import `check_model_inputs`. Use a separate Qwen interpreter with compatible Transformers and Hub versions. An environment may reuse existing CUDA packages through a `.pth` entry and keep its conflicting packages local; this avoids downloading PyTorch again but depends on the shared environment remaining available. Import/CUDA checks establish dependency readiness, not successful model synthesis.

The experimental `voice-clone` block uses a project audio file as its voice reference and speaks its `text` parameter. A pause is written in the text: `I'm walking [pause 800 ms] down [pause 1.2 s] this road` is made per piece with the same reference and AMPLE puts the silences between them (at most 10 s per pause and 60 s together; a pause only at the start or end is allowed). To put pieces of different blocks one after the other use a `join` block, and to start a sound later by seconds or beats a `shift` block: a placement (`at <bar> for <bars>`) is in whole bars. XTTS and Chatterbox need no reference transcript. Qwen3 requires `reference_text`; when that is blank, `auto_detect on` runs faster-whisper. The block refuses to build when Qwen3 has neither. MP3 and other accepted formats are converted to mono 24 kHz WAV with the configured ffmpeg when the engine requires it. Configure the local Python executable, model root and ffmpeg in the plugin settings. On first use, the plugin prepares an isolated Python environment and installs the selected engine's packages there; model weights are not downloaded. Qwen3 does not support Dutch or Flemish. Generated WAVs are checked for samples, finite values and non-silence; empty or silent output fails the build with an explicit error. A plugin version upgrade invalidates the previous voice-clone takes once. Speech quality and GPU runtime remain unverified until a build is run with the selected local model. Loading and synthesis progress explicitly show the selected GPU (CUDA) or CPU device, including long-running heartbeat updates; a CUDA check in another environment does not verify the active worker.

`plugins/analysis-local-transcription` is an optional local adapter for the transcription wizard. It expects `audio-separator`, `basic-pitch`, `soundfile` and `scipy` to be installed in a local Python environment and requires a complete, preinstalled Audio Separator model folder. Nothing is bundled, installed or downloaded. The compute-device choice defaults to GPU and requires explicit permission for each job; choose CPU to hide CUDA from the worker. GPU use requires GPU-enabled dependencies in the selected Python environment; otherwise GPU mode may fall back to CPU. The worker sets offline environment flags and blocks Python socket connections; missing model companions cause the job to fail instead of downloading them.

In the wizard, select **Local stem separation and MIDI estimate** and provide options such as `{"pythonPath":"C:\\\\Python310\\\\python.exe","modelDirectory":"C:\\\\AudioModels\\\\UVR","separatorModel":"UVR_MDXNET_KARA_2.onnx","modelLicense":"license name or URL","drumMode":"skip"}`. The model and all companion files must already exist in that folder. The result records the model SHA-256 and license text supplied in options; the UI labels the latter as user-entered, unverified metadata. The adapter aligns returned stems to the source duration, asks Basic Pitch for vocal and accompaniment MIDI estimates, and returns vocal pitch activity for review. When the selected model returns separate accompaniment stems, it emits one MIDI artifact per stem and a combined multitrack MIDI artifact for one-step import, and suggests editable GM programs from safe stem labels; a single instrumental stem produces one backing artifact. Stem labels are not instrument recognition, and the clamped 0–1 Basic Pitch amplitude is not calibrated confidence. Drum stems are omitted by default because Basic Pitch does not map drum hits; set `drumMode` to `pitched` to request a rough pitched estimate. Runtime and transcription accuracy have not been measured on this machine. Model terms are separate from package licenses; verify the selected model's terms before use.

## What the host guarantees
- A **clean environment**: nothing inherited except `PATH` and the few variables a program needs to start; a declared secret is passed only to the plugin that declared it, and its value is **redacted from captured logs**.
- A private **job folder** (deleted afterwards); an output path outside it is refused (`AMP5272`).
- **Timeouts** (`AMP5276`), **cancellation** (the process tree is stopped), **crash containment** (`AMP5275`), invalid output (`AMP5273`), oversized messages and outputs (`AMP5274`), plugin-reported errors (`AMP5278`). A failed render is a failed attempt in the takebank: the plan stays, there is no take, and the next build can try again.
- **One GPU job at a time** for plugins that need the GPU.
- The plugin's description (toolprint) and determinism claim are stored in the plan of every take it makes, so regeneration warnings and reproduction checks know what produced a take.

## Writing a plugin
`plugins/sdk/ample-plugin.mjs` is a small Node helper (`serve({initialize, describe, validate, render})`); `plugins/echo` is the reference plugin (deterministic, no GPU) and `plugins/flaky` misbehaves on purpose (crash, hang, garbage, huge output, path escape, errors, logging a secret) for the host tests. Any language works: read lines, answer lines.

## Commands
`ample plugins list|describe <id>|validate <folder>|run <id>` (`run` is a self-test: handshake and description).

### Voice-clone warm cache (2026-10-10)

`voice-clone.keepWarmS` (default 600, 0 = off) keeps one Python worker per interpreter alive and the loaded model (and Whisper) in memory, so a rebuild shortly afterwards goes straight to synthesis instead of loading the model again. Only one engine is kept: asking for another engine unloads the first.

The worker gives the memory back by itself and logs why (`voice model cache: evict (reason)`; the log holds the event, the reason, counts and megabytes only):

- idle for longer than `keepWarmS`;
- free video memory of the device below `voice-clone.minFreeMb` (default 1000);
- video memory used outside this worker grew by more than `voice-clone.pressureMb` (default 1024) since the model was loaded. This is computed from the device totals (`torch.cuda.mem_get_info`) minus what the worker holds itself; it never looks at other processes, their names or their arguments.

A pressure check only runs between builds, never during synthesis. A timeout or a crash ends the worker and so empties the cache. When the generic `python` default re-launches into the plugin's own isolated environment, that one-shot child has no cache: use an explicit interpreter (per engine) to benefit. Not measured on real hardware yet: the speed-up and the eviction on real VRAM pressure.

Contention while a build runs (added the same day): the worker keeps looking at the device totals while it loads and synthesizes. When it sees pressure (free memory below `minFreeMb`, or memory outside the worker grew by more than `pressureMb`), the running build is allowed to finish, and the model is unloaded right afterwards instead of being kept (log: `contended`, then `evict`). All three values and `keepWarmS` are plugin settings. Measured on one machine (8 GB card, `torch 2.6.0+cu124`): the totals are readable (`mem_get_info`), and allocating 300 MB inside the worker does not change the "outside this worker" figure, so the worker's own use is not mistaken for another program. Not yet measured: a real load that competes with another program.

### GPU plugins kept warm by the host (2026-10-10)

The host starts a plugin process per job and used to stop it afterwards, so nothing a plugin kept in memory survived to the next job. A plugin that needs the GPU and declares the setting `keepWarmS` (seconds, 0 = off) is now kept running by `PluginHost` after a successful render and reused by the next job of the same plugin; it is stopped when it has been idle for `keepWarmS`, when `PluginHost.ReleaseWarm` is called (for a "free GPU memory" action), when the host stops, or when a job fails or times out (a failed job's process is never kept). The events ("kept for up to N s", "reused", "released (reason)") go to `PluginHost.OnWarmEvent`; they name the plugin and a reason, nothing about any other process. Only `voice-clone` declares `keepWarmS` so far (default 600).

The host also watches the memory totals of the GPU while it keeps a process (NVML `nvmlDeviceGetMemoryInfo`, fallback `nvidia-smi --query-gpu`; totals only, never processes): every five seconds it releases a kept process when free memory is below the plugin setting `minFreeMb` (default 1000) or when the memory in use grew by more than `pressureMb` (default 1024) since it was kept (our idle processes do not grow, so growth is another program). Every keep, reuse and release is appended to `.ample/logs/gpu.log` (time, plugin, reason). `GET /api/gpu` returns the totals and what is kept; `POST /api/gpu/release` gives the memory back now (the button "Free GPU memory now" in the plugin settings view). The GPU settings are the voice-clone plugin's `keepWarmS`, `minFreeMb` and `pressureMb`.

Not kept warm on purpose: ACE-Step (its server backend keeps the models loaded in the server you run; the command line backend starts the program per job, so a kept Node process would hold nothing) and the local transcription plugin (it runs through the analysis path, blocks the network and asks permission per job for the GPU, so a long-lived worker would go against that). Only the first GPU is looked at.

When the environment of an engine cannot be used, the error names which setting chose the interpreter and which setting to change (for example: "The chatterbox engine runs in the Python from voice-clone.python. It is missing: ... Set voice-clone.pythonChatterbox to a Python that has them"). Nothing is installed or switched automatically.
