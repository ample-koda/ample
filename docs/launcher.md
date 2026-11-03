# Opening AMPLE projects

`ample open <project-folder>` attaches to a registered server for that project or starts one in the background, waits for its local liveness check, and opens the private launch URL in the default browser. `--no-open` prints the launch URL instead. A `.amplet` or `.module` target resolves to its containing project. With no target, `ample open` lists recent projects; `--last` selects the newest one.

The last successful port is saved in `recent.json`. A later open tries that port first and chooses a free port if it is already occupied. Child server output goes to `<AMPLE_HOME>/logs/<project-id>.log`; each log rolls at 1 MiB and retains two older files. Launch tokens are kept in the per-user `.launch` file and are not written to the server log. Set `AMPLE_HOME` or pass `--home` to use a different writable user-data folder.

`ample open --agent <name> --reason <text>` records self-reported agent attribution. When the sibling `Ample.Hub.exe` is installed, the CLI starts it if needed and sends a correlated v1 JSON-lines request over the current-user-only pipe. When the hub is not installed or reachable, agent starts use the visible CLI fallback. `AMPLE_AGENT` and `AMPLE_AGENT_REASON` provide the same attribution through the environment. The hub pipe's versioned fields and limits are documented in the L05 plan; the pipe server is implemented by L05.

`tools/AMPLE.bat [target]` is the Windows double-click/command launcher. It uses a packaged `ample.exe` beside the batch file or at the package root one folder above; otherwise it runs the CLI project from source. The classic `ample serve` command remains a foreground server, opens the browser by default, and prints its launch URL as before. If the instance registry cannot be written, classic serving stays active but `ample open` cannot discover that server.

The optional `--song` value is validated by the CLI, but the web UI does not yet consume a song deep link.
# Packs and ACE-Step settings

AMPLE looks for installed packs in this order: `AMPLE_PACKS`, `packsFolder` in `%LOCALAPPDATA%\ample\settings.json`, `%LOCALAPPDATA%\ample\audio-packs`, the AMPLE root supplied to `serve --root`, then ancestors of the executable and project. `ample packs list` shows that order; `ample packs where` explains a missing General MIDI bank; `ample packs link <folder>` creates a junction instead of copying an existing `audio-packs` folder.

The fetch commands install into `%LOCALAPPDATA%\ample\audio-packs` by default. Add `--into` to retain the repository `audio-packs` layout. The same settings file can contain `ace.exe`, `ace.models` and `ace.port`; `tools/start-ace-server.bat` reads those values or the `ACE_EXE`, `ACE_MODELS` and `ACE_PORT` environment variables.
