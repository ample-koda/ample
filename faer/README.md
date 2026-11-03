# Faer service

**Status (2026-10-16):** the Trooper.ai service reports ACE song, ACE voice, voice cloning and Demucs separation configured. Voice packages import and all three voice environments report CUDA available. A one-bar `ace-music` block completed through AMPLE and Faer on the Trooper GPU; after Faer's 60-second warm period, its ACE port closed and the regular test returned to 1 MiB used. Four XL Q5_K_M variants are installed, and a one-bar XL SFT render completed. Its ACE port also closed after unload, but `nvidia-smi` still reported 12,773 MiB used with no compute-app rows, so XL VRAM release remains unresolved. Long-form output quality, the other capabilities and cancellation still need acceptance checks. Faer now returns raw worker failure details to AMPLE and its server log. See the bridge plan and operations notes.

The service uses Node.js 22 or newer and only built-in modules. It binds to `127.0.0.1:3888` by default. Run it with a dedicated private data directory:

```bash
FAER_DATA_DIR=/path/to/private/faer-data node faer/server.mjs
```

`FAER_TOKEN_FILE` may name a file containing one bearer token. Do not put the token on the command line. `FAER_BIND` changes the listening address; a non-loopback address requires `FAER_ALLOW_REMOTE=1`, a token file and TLS certificate/key files. The Trooper.ai deployment uses public HTTPS on its assigned port `31146` with a machine-local certificate pin and bearer token. `FAER_PORT` changes the port.

The default Faer storage quota is 20 GiB (`assets/`, partial `uploads/`, active `jobs/` and cache index). Completed ACE song and ACE voice renders also have a persistent exact-request LRU index capped at 4 GiB of output references, 128 entries and 30 days since last use. Eviction removes cache references; content-addressed assets remain available until they are otherwise removed and the whole asset store remains bounded by the 20 GiB quota. Set `FAER_RENDER_CACHE_BYTES`, `FAER_RENDER_CACHE_ENTRIES` or `FAER_RENDER_CACHE_EPOCH` to tune capacity or invalidate old renderer/model identities after an operator-managed change. An exact matching request returns its verified result immediately without queueing GPU work. Send `forceRender: true` to bypass the lookup, run the worker, and refresh that request's entry. AMPLE's normal Studio build uses the cache; **Force full render** on an ACE song/voice block and Codex `ample_build(forceRender=true)` bypass both AMPLE's take cache and Faer's remote cache. One asset is limited to 4 GiB and one upload chunk to 8 MiB. Uploads are serialized to protect the storage budget. The cache limit does not reserve filesystem space for models or Python environments. The server does not download models or install packages.

## Current HTTP contract

| Route | Result |
| --- | --- |
| `GET /v1/status` | Protocol version, aggregate storage limits and readiness by capability. Readiness means configured worker presence, not proven GPU inference. |
| `GET /v1/assets/{sha256}` | Whether an asset is complete and how many bytes of a partial upload are present. |
| `PUT /v1/assets/{sha256}` | Append one chunk using `X-Faer-Total` and `X-Faer-Offset`; a final chunk is published only after its SHA-256 matches. |
| `GET /v1/assets/{sha256}/bytes` | Download the complete asset as a byte stream. |
| `POST /v1/jobs` | Queue a job for an installed worker, or immediately return an exact completed ACE render (`cacheHit: true`). `forceRender: true` bypasses the render cache. The client may assign a UUID before submission so cancellation still works if the response is lost. |
| `GET /v1/jobs/{id}` | Read job state, queue position, progress, completed result, or failure code/stage plus the raw worker reason and stack details. Failure details are also written to the Faer service log. |
| `DELETE /v1/jobs/{id}` | Cancel a queued or running job and signal its worker. An early cancel of a client-assigned UUID prevents a late submit from starting it. |

Bearer authorization, when configured, applies to every route. The service runs one worker job at a time and retains at most 128 jobs; terminal jobs are removed oldest first when that limit is reached. If all retained jobs are active or queued, a new submission receives HTTP 429. A worker may report only the fixed stages `preparing`, `planning`, `synthesis`, `isolation`, `voice`, `separation` and `verifying`; failed jobs additionally expose the raw worker reason and stack, which may include internal paths or upstream diagnostics, to the authenticated AMPLE client and the Faer service log. Queue positions count from one. The Node tests inject synthetic workers and data; they do not prove real GPU operation, AMPLE integration, persistent job recovery after a service restart, or safe deployment. Run the focused checks with `node --test faer/server.test.mjs faer/gpu-manager.test.mjs`.

GPU workers run in one serial chain. Compatible ACE capabilities share one loaded server; switching workers unloads the previous one first. The last worker stays warm for 60 seconds, then unloads, and shutdown cancels an active job before unloading. Only errors coded `FAER_GPU_BUSY`, `FAER_GPU_OOM` or `FAER_GPU_UNAVAILABLE` trigger an unload and three short retries (1, 3 and 6 seconds); unrelated failures fail immediately. A failed unload marks the GPU manager unavailable until restart. These policies were covered by synthetic lifecycle tests before the latest integration changes. Live process and VRAM release still need verification.
