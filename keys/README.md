# AMPLE public keys

Public keys only. The private keys are not in this repository.

| Job | Key id | File | Made |
|---|---|---|---|
| Release signature (active) | `d0ebf7d9c4d8b7ce` | `ample-release-d0ebf7d9c4d8b7ce.pem` | 2026-11-05 |
| Release signature, spare key (only used if the release key is lost) | `597b43377806b15c` | `ample-recovery-597b43377806b15c.pem` | 2026-11-05 |

ECDSA P-256, made with `ample keys generate`.

## apt repository key (beta)

| Job | Fingerprint | Files | Made |
|---|---|---|---|
| Signs the apt repository `https://apt.ample-koda.dev` (beta key, RSA 4096, valid for two years) | `A7C6 4C7D FFE6 8599 4233  336B 1068 8A60 3C03 86C7` | `ample-apt-beta.asc` (armored), `ample-apt-beta.gpg` (binary, for `signed-by=`) | 2026-11-05 |

This is a beta key. It will be replaced by the final repository key; apt users then add the new key the same way.
