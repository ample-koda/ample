# Access outside the project: what the assistant may do and how it asks

> Status (2026-10-11): built. For the assistant and for people.

The assistant (Codex) works in a **copy** of the project. Inside that copy and in its `./scratch` folder it needs no permission. Everything else needs the person's say-so.

## For the assistant
- **Remarks about AMPLE** (what is missing, what could be better, how to fix it): `ample_findings { text, name? }`. It writes to `notes/ai-findings/` inside the project. No permission needed. Say where it is in your last message.
- **A file somewhere else** (a full path such as `C:\temp\x.txt`): first `ample_request_access { path, why, minutes, kind? }`. The person sees what, why and for how long, and answers *allow* (for that long), *always for this project* or *deny*. After an allow use `ample_write_outside { path, text }`. Never try another way to write there.
- **If you try anyway** (a command or a file change outside the working folder) AMPLE **stops the run** at that request and asks the person: *allow once*, *always for this project*, or *ask it to do it correctly*; in the last case you are told to use `ample_findings` or `ample_request_access`.
- **What is missing**: when a tool answers with a `hint` (a PDF reader, the voice worker, ffmpeg), tell the person and recommend the hint. `ample_project_log` lists them with how to fix.

## For people
- The question appears in the task window with the path, the reason and the time. *Always for this project* is kept in `.ample/ai/grants.json` and listed (with *Revoke*) under Projects, Health.
- `ample doctor --env` checks what the tools need around the program (libraries, ffmpeg, Python, the sound font, free disk space) and writes what is missing, with how to fix it, to the project log (`.ample/logs/project-log.jsonl`, shown on the Health page).
- A grant of kind *passthrough* only allows AMPLE's own `ample_write_outside`, never a direct write.
- Never open for writing: the project's own `.ample` folder and the system folders.

## Codes
AMP5610 (a path that can never be allowed or is not a full path), AMP5611 (no permission for that path), AMP5458 (the task is no longer running: AMPLE was restarted or the task ended; start a new task, nothing is lost in the project).
