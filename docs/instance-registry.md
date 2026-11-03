# Instance registry

AMPLE records each running project server below `instances/` in its AMPLE home: `%LOCALAPPDATA%\ample` by default, or `AMPLE_HOME` when set. The record format is [defined by the JSON Schema](../spec/instance-registry.schema.json). The `.launch` sibling contains the current browser URL and token; it is restricted to the current user (Windows ACL without inherited rules, Unix mode `0600`). The token never appears in the JSON record or `ample instances` output.

Each record contains the project id and name, folder, server pid and process start time, port, start/update times, AMPLE version, machine name and start source. Agent starts include their self-reported name, reason and optional session. ACE health is optional. `ample instances` lists only records whose process still has the recorded start time and whose port answers `GET /api/ping` with the same project id. `ample instances --clean` removes stale record and launch files; `--json` emits records without launch URLs. `--home <folder>` selects a registry home for isolated use and tests.

The server registers after it has bound its port, refreshes `updated` every 15 seconds and removes both files during clean shutdown. A crash can leave files behind; readers treat them as stale, and `--clean` removes them. `GET /api/ping` returns only `{ "ok": true, "id": "<project-id>" }` without a token. `POST /api/server/shutdown` uses the normal session-token requirement.

Verification covers fake process and ping probes, atomic concurrent writes, stale cleanup, private launch-file permissions and the live server ping and token-gated shutdown route. The registry does not prove that an agent's self-reported identity is authentic.
