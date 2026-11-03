# Security review checklist

Status (2026-10-03): written in chunk 98 from what is built and tested. **The owner has not signed it off yet** (the acceptance item of chunk 98 needs that). Everything marked "not verified" is listed at the end.

## Server (`ample serve`)

| Defence | Proof |
| --- | --- |
| Listens on 127.0.0.1 only; a LAN address needs `--allow-lan` (documented as trusted network only) | `docs/api.md` Safety; the smoke test starts the packaged program and reaches it on 127.0.0.1 |
| Host header must be localhost or 127.0.0.1 (DNS rebinding), AMP5282 | `ServerTests.Foreign_host_header_is_refused` |
| Foreign Origin refused, AMP5283 | `ServerTests.Foreign_origin_is_refused` |
| Session token on every `/api` call, AMP5284; the token travels in the URL fragment (never sent to a server); a cookie for GET only | `Api_without_token_is_refused`, `Wrong_token_is_refused`, the WebSocket test |
| Bodies limited to 8 MiB, `no-store`, `nosniff` | `Oversized_body_is_refused`, `Project_is_served_with_security_headers` |
| Paths stay inside the project (no `..`, no absolute paths, no reserved names, no symlink escapes) | `Path_outside_the_project_is_refused`, `ProjectPath` tests, `/api/file` refuses other files |
| Dangerous acts need a dry run, a one-use token, the typed words and a reason; direct confirmation fails | `A_dangerous_act_cannot_be_confirmed_without_a_dry_run`, the dangerous-act parity spec (API bypass attempts) |
| Errors are RFC 9457 problem details with an AMP code; no stack traces | `ServerSupport.Problems` |

## Plugins

| Defence | Proof |
| --- | --- |
| A clean environment: only PATH and the few variables a program needs to start | `HostTests.The_plugin_gets_a_clean_environment_and_only_the_secrets_it_declared` |
| Declared secrets only, redacted from captured logs | `A_declared_secret_reaches_the_plugin_and_is_redacted_from_its_logs`, the vault canary test |
| Outputs must stay inside the job folder; size limits; timeouts; a crash, garbage or a hang gives a failed attempt, never a take | `HostTests` (path escape, huge output, hang, garbage, crash) |
| One GPU job at a time; an analysis plugin that wants the GPU or the network is not started without a yes | `A_plugin_that_wants_the_gpu_or_the_network_is_not_started_without_a_yes` |
| Plugins are pinned by hash in `ample.lock` | `LockFile` tests |
| **Not a sandbox:** a plugin is a normal process with the user's rights; the environment and path rules limit mistakes, not a malicious program | stated here; install only plugins you trust |

## Secrets and data at rest

See [Security](security.md) for the threat model, the vault, signing and the optional encryption (AES-256-GCM, PBKDF2, ECDSA P-256 from the .NET platform; no custom cryptography). Tests: the canary-secret scan of the project, the black box, a backup and an export; wrong-passphrase, tamper and key-loss paths.

## Dependencies (audited 2026-10-03)

- `dotnet list package --vulnerable --include-transitive` on the solution: **no vulnerable packages** (against the NuGet vulnerability feed of that day).
- `npm audit --omit=dev` in `ample/`: 0 vulnerabilities.
- `npm audit` in `editors/vscode`: 6 high findings, all in the chain `@vscode/vsce` > secretlint > globby > fast-glob > micromatch > braces (a denial of service by deeply nested glob patterns). They are in the **packaging tool only**, which runs on the developer's or the release machine on trusted input; nothing of it is shipped in the `.vsix`. The runtime dependency `vscode-languageclient` has no finding. Revisit when `@vscode/vsce` moves its dependency.
- Re-run both audits before every release (`tools/package.ps1` does not; the release workflow does).

## Not verified, or left to the owner

- Sign-off of this checklist by the owner.
- No licence file is chosen: the repository and the extension say `UNLICENSED`. Picking a licence is the owner's decision.
- No external penetration test; no fuzzing beyond the parser and format tests.
- The server has not been run behind a reverse proxy or with `--allow-lan` on a real network.
- Linux and macOS: the Linux package is built, but the Linux test run has not been seen yet (the CI matrix includes it; see [Release](release.md)).
