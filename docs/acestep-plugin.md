> Status (2026-10-03): built against a **stand-in server**. The `/lm` → `/job` → `/synth` transport is regression-tested locally, but real-server audio behavior remains unverified and needs the owner's GPU approval.

# ACE-Step plugin

`plugins/acestep` turns `ai-voice` and `ace-music` blocks into requests to an acestep.cpp server and brings the audio back as 16-bit mono PCM at the project's sample rate, cut or padded to the block's length. `ace-music` may intentionally include accompaniment and may be instrumental when no lyrics are linked. `ai-voice` first requests an a-cappella performance and then extracts its `vocals` stem from the resulting mix, so the delivered take is the extracted stem rather than the initial music-generation result. The manifest says `determinism: unverified`, so regeneration warns (AMP5101 and AMP5102) until a reproduction check has come out the same.

## Inputs and meaning
- **Lyrics editor file** is the canonical lyric source. Its timed markers are removed before ACE receives the words. `ai-voice` refuses to render without it; `ace-music` accepts no lyrics.
- **Vocal direction** is written performance guidance, not an identity picker or a promise of a named singer.
- **Mood / scene** arrives through `style` and is appended to the ACE caption.
- **Melody** arrives as the MIDI clip's JSON at the configurable `melodyField` (default `melody`) in the `/lm` request. Set that field to the exact server revision's documented conditioning property before a real run; the linked edge is recorded as `melodyPlan: requested`, rather than being silently ignored.

## Download formats
Built WAV takes download without conversion. MP3 export runs the configured `AMPLE_FFMPEG` executable (or `ffmpeg` on `PATH`) with `libmp3lame`; the response identifies the exact encoder banner in `X-Ample-Mp3-Encoder`. If the encoder is unavailable or fails, AMPLE returns AMP5290 rather than serving a renamed WAV.

## What it sends and expects (ACE-Step server API)
| Step | Request | Answer |
| --- | --- | --- |
| Plan | `POST /lm` with JSON caption, lyrics, language, seed and duration | `{ "id": "..." }` |
| Render | `POST /synth` with the completed plan | `{ "id": "..." }` |
| Voice-only extraction | `POST /synth` as multipart form data: extraction request plus the rendered WAV in `audio` | `{ "id": "..." }` |
| Poll/fetch | `GET /job?id={id}` then `GET /job?id={id}&result=1` | job state, then the multipart audio result |

The extraction request uses `task_type: "extract"`, `track: "vocals"` and a Base or SFT DiT model. It is on by default for `ai-voice` (`vocalExtraction: true`). The plugin discovers a compatible model through `GET /props`; the first Base or SFT model that `/props` lists wins, which is a light (non-XL) one when you have it. Set `vocalExtractionModel` (the file name as `/props` lists it; a missing `.gguf` is added) when you want a specific model, for example an XL one for more detail. The model used is written into the take's metadata (`vocalIsolationModel`). A Turbo-only server fails before it creates a take, rather than returning a music-backed result labelled as vocals. An optional bearer token is read from the secret `ACESTEP_TOKEN`.

## Rules the plugin follows
- Only a **local endpoint** (`localhost`, `127.0.0.1`, `::1`) unless the setting `allowRemote` is on.
- **Lyrics, prompts and file names never go to the logs**; only "job submitted" style messages do.
- The seed is the block's derived seed (or the locked seed from the seed history); `lm_seed` and `steps` come from the block parameters.
- The result records whether a melody was requested and whether vocal extraction ran; it does not claim that unverified MIDI conditioning was honoured by a particular server revision.
- Errors become plugin errors with a plain message (server unreachable, HTTP status, answer not JSON, job failed, timeout), never a hang: the host's timeout and cancellation still apply.

## Trying it without a GPU
`node ample/plugins/acestep/stub-server.mjs` prints a port and answers with a tone (modes: `ok`, `nobase`, `error500`, `malformed`, `neverdone`, `noaudio`). Point the setting `endpoint` at it. The stub models the request contract only; it cannot demonstrate audio separation quality.

## Manual verification on the real server (to be run by the owner)
Needs a running `ace-server` (GPU in use, so confirm first) and a tunnel to it.
1. Confirm `/props` advertises a Base or SFT DiT model, or set `vocalExtractionModel` to one installed model file name (ace-server only accepts the name exactly as `/props` lists it; before 2026-10-03 the plugin cut off the `.gguf` and every extraction failed at once).
2. Make a project with one `ai-voice` block and a `lyrics` block, build it, and listen: the take should be the extracted vocal stem, the right length and mono.
3. Run `ample verify-reproduction` on the block: **same** means the server is reproducible for that seed on this machine (the plugin's determinism can then be recorded as verified-local); **different** means regeneration will always ask.
4. Note the server commit, the model files and the GPU in `docs/kb` with the date.

## Isolating the voice: ace, demucs or off (plugin 0.4.0, 2026-10-03)

The setting `vocalIsolation` chooses how the voice is separated from the music that ACE-Step makes:

| Value | What happens | Needs |
|---|---|---|
| `ace` (default) | A second ACE job with `task_type: extract`. Works with a light Base or SFT model (`acestep-v15-base-Q4_K_M.gguf` 1.45 GB, `-Q5_K_M` 1.70 GB, the same for `sft`) and with the heavy XL ones. Set `vocalExtractionModel` to pick one. | A Base or SFT DiT in the server's models folder |
| `demucs` | Demucs (`htdemucs`, two stems) runs on the CPU in a Python of your own: `python -m venv demucs-venv`, then `pip install demucs soundfile`, and set `demucsPython` to its `python.exe`. | The Python, about 2 GB for torch and the model |
| `off` | Take the synthesis as it is: fastest, may keep some music. | Nothing |

Set `modelsFolder` to the folder of the server's models and the take records the sha256 of the model that was used (`vocalIsolationSha256`), so a sound can be traced to its exact model; for Demucs the hash is that of the checkpoint in the torch cache. All four light ACE models and the XL base model were tested end to end on 2026-10-03 (RTX 3070, 8 GB); the models come from Hugging Face `Serveurperso/ACE-Step-1.5-GGUF` and their sha256 matched the repository's. `tools/start-ace-server.bat` starts the server in a window of its own.

## Choosing the model that makes the sound (plugin 0.5.0, 2026-10-03)

A block can choose its own model (and where it runs) with its `engine` parameter, for example `engine "faer:xl-sft"`; that wins over `synthModel` and `synthModelFile` in the song settings for that block. `synthModel` picks the ACE-Step model for the synthesis: `default` (an explicitly resolved turbo model, 8 steps), `turbo`, `sft` (cleaner and softer voices, about 50 steps, slower; the usual choice for singing), `base` (the raw model, about 50 steps), or the large `xl-turbo`, `xl-sft` and `xl-base`. For automatic family selection, the plugin lists the models the server offers (`GET /props`) and takes the best file of that kind: `Q5_K_M` first, then `Q6_K`, `Q8_0`, `Q4_K_M` and `BF16`. `synthModelFile` names one file exactly and wins. On the server backend, an explicit filename is sent directly even if `/props` omits it, so ACE itself can accept or reject the attempt with its actual response; on the CLI backend it must be present in `modelsFolder`. The `steps` parameter defaults by family (turbo 8, SFT and base 50). Both ACE blocks expose `guidance` as CFG, visibly resolved to 7 for Base/SFT and 1 for Turbo; that exact value is sent as `guidance_scale` and recorded in take metadata. Changing CFG can change the generated result, so use the same value for seed comparisons. `shift` is also exposed (SFT 1, Base/Turbo 3); `solver` defaults explicitly to euler. The model that was used is written into the take (`synthModel`). Model selection remains in plugin settings.

## Backend: server, command line tools or both (plugin 0.6.2, 2026-10-17)

Generation recipes must survive the complete path into the immutable take; seeds alone are insufficient. See the 2026-10-17 near miss and review checklist. LM sampling is explicit: temperature 0.85, CFG 2, top-p 0.9, top-k 0 and an empty negative prompt. `lmModelFile` selects the LM explicitly for either backend; the legacy `cliLmModel` remains accepted. Otherwise an advertised 4B Q8 model is preferred, then a stable sorted selection. The resolved LM and synthesis requests, returned plan and available hashes are retained. These defaults are a reproducible baseline, not a listening-quality guarantee.

The setting `backend` says how the sound is made, and **every song states it itself** (the schema marks it `x-songRequired`; the project value is only the default for new songs, which is `server`):

| Value | What happens |
|---|---|
| `server` | ace-server on `endpoint` (it must be running). |
| `cli` | The acestep.cpp tools `ace-lm` and `ace-synth`, run by the plugin (needs `aceBinFolder` and `modelsFolder`; `lmModelFile` picks the language model; the selection policy is described above). |
| `server-then-cli` | The server when `GET /health` answers, otherwise the command line tools. |

Same seed, same models and same request give the same audio on the server and on the command line tools: on 2026-10-04 a 30 s demo (turbo Q5, language model 1.7B) gave the same sha256 for the server (twice) and for `ace-lm` plus `ace-synth` on this machine. Not checked: other models (sft, XL), other graphics cards or builds, a freshly started server; so the takes stay `unverified`. The take metadata records the `backend` that was used.

A song without `acestep.backend` gets the warning `AMP2013` in the editors with a quick fix ("Add acestep.backend (the project default)", also in Fix all) that writes `settings { acestep.backend server }`; a build of such a song fails the block with `AMP5370` and the same advice, which also appears in the build log.

## A voice from a recording (reference)

`ai-voice` and `ace-music` take an optional **reference** audio input (link another block, for example an ACE block with another seed, or set `voice_sample` on `ai-voice` to a project file; the link wins). Measured 2026-10-22 on the local server (acestep.cpp b7ba6d9) with the SFT model, two female vocals:

- Reference only (ACE `ref_audio`, the timbre encoder): the server receives and encodes it, but the voice stays close to what the `voice` text and the seed decide. Weak.
- Reference plus `voice_strength` (a cover of the reference, `task_type cover`, the language model codes dropped): the voice clearly follows the reference (owner's listening test with strength 0.9). The melody follows the reference too; `reference_noise` (cover noise) lets it drift.
- Faer cannot take a reference yet: AMP5384, use `engine local`. The reference file/audio hash is part of the plan.

Measured on 2026-10-22 (same server, SFT model, one female reference of 19 s, owner's listening and a pitch-contour check):

- A cover (`voice_strength`, also with `reference_noise`, `reference_melody new` or a low strength such as 0.15) always keeps the melody and the register of the reference (contour correlation 0.4 to 0.56, register equal); those three knobs change this hardly or not at all on this server revision. A cover is therefore a way to sing the same line again with the same voice, not to put the voice in a new song.
- A new scene with the voice of the reference needs the timbre route (the reference input without `voice_strength`). The voice is clearly recognisable there; a reference of 19 s, 10 s or 5 s made no audible difference in the owner's test.
- The first test sounded shrill and echoey with every reference length and also without a reference, so it came from the `voice` text and not from the reference: "raw powerful energetic rock vocal, fast and aggressive, gritty" at CFG 7. A soft direction (for example "powerful warm rock vocal, smooth and full, natural, close microphone, no harshness") was reasonably good; CFG 4 and 3 were not preferred.
- The register still follows the style text (a rock direction sang about 6 semitones lower than the soft reference).

Note (2026-10-22): the parameters `reference_noise` (sent as `cover_noise_strength`) and `reference_melody` (`new` kept the codes of the language model plan) were built and measured, then removed because they changed nothing audible on this server revision. They can come back if a newer acestep.cpp makes a cover follow them.

Adapter (LoRA) test, 2026-10-22: the server must be started with `--adapters <folder>` (then `/props` lists the adapters). With the usual Q5_K_M models it fails (`ggml_cuda_cpy: unsupported type combination (q5_K to f32)`, the synthesis job fails); only `acestep-v15-base-BF16.gguf` worked with `acestep-vocals-lora` (strength 1.0 and 0.5). The owner judged the result "not really good", so the adapter is not used; the parameters `adapter` and `adapter_scale` stay available for other adapters.

## Time limit, meter and gaps

- `timeoutS` (default 900; an invalid value also means 900) is one deadline for the whole render: setup calls, planning, synthesis, extraction and Demucs. Every server call, wait and child process is limited by what is left, and the error names the stage (`plan`, `synthesis`, `vocal extraction`, `demucs`). Demucs and the command line tools run as child processes that cancel and the deadline stop.
- When the build is cancelled or the deadline passes while a server job runs, the plugin sends `DELETE /job?id=...` as a best effort (2xx is logged as "asked to stop", anything else as "could not ask"; the job may still run). This route is not confirmed against ace-server; the stand-in server records it.
- Meter: only 2, 3 or 4 quarter notes per bar are sent (2/4, 3/4, 4/4); 6/8 arrives as 3 quarter notes and is sent as 3/4. 5/4, 6/4 and 7/8 are refused before any server request. The `bpm` sent is the quarter-note tempo from the bar length.
- Gaps: ACE-Step makes one continuous region, so a block whose active bars have a hole is refused before any server request. Duplicate bars are not a hole.
