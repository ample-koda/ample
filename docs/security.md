# Security: the vault, signing and optional encryption

Status (2026-10-03): built in chunk 92. This page says what is protected, what is not, and how to use it.

## What is built

| Part | What it does | Where |
| --- | --- | --- |
| **Vault** | Secrets (API keys, tokens) live in one file of your library folder (`vault.json`), sealed with a key made from a passphrase. Projects and plugin manifests only carry the **name** of a secret. A plugin gets only the secrets its manifest declares, as environment variables, and the value is redacted from its captured logs. | `ample vault set\|list\|remove`, `Ample.Storage.Security.PassphraseVault`, `Ample.Plugins.VaultSecretProvider` |
| **Signing** | You sign the newest record of a project's black box (or a file such as an export) with your own key. `ample verify` checks the hash chain **and** every signature: a changed record, a cut-off journal, a forged signature log or an unknown signer is reported (AMP5312). | `ample keys generate\|list\|export\|rotate`, `ample sign`, `ample verify` |
| **Encryption at rest (optional, off by default)** | Every record of the black box (and its mirror in your home folder) is sealed; backups made from then on are sealed file by file. A data key is wrapped by your passphrase; changing the passphrase re-wraps the key and re-encrypts nothing. | `ample encrypt status\|enable\|disable\|recovery-kit\|passphrase` |

Passphrases and secret values are **never arguments** (they would show in process lists and shell history): they come from environment variables (`AMPLE_VAULT_PASSPHRASE`, `AMPLE_PASSPHRASE`, `AMPLE_NEW_PASSPHRASE`) or from standard input (`echo the-secret | ample vault set NAME`).

## Cryptography used (no custom primitives)

| Need | Choice | Why |
| --- | --- | --- |
| Sealing | AES-256-GCM (.NET `AesGcm`), random 96-bit nonce per record and per 1 MiB file frame | Authenticated; part of the platform. XChaCha20-Poly1305 would need an extra library. |
| Passphrase to key | PBKDF2-HMAC-SHA256, 600 000 iterations, random salt | Part of the platform. Argon2id (memory-hard) would be better against GPUs but needs an extra library; revisit. |
| Signatures | ECDSA P-256 with SHA-256 (.NET `ECDsa`), IEEE P1363 format | The platform has no Ed25519. |
| Random | `RandomNumberGenerator` | Operating system source. |

## The threat model

**Protected**
- A stolen laptop, a copied project folder or a backup drive in the wrong hands cannot show the **black box** (what was deleted, replaced or written, with the pre-images) without the passphrase, when encryption is on. Neither can the encrypted backup objects.
- A secret never reaches a project file, a journal record, a backup or an export: commands carry the secret's name, never its value, and the vault file keeps values sealed. A canary-secret test scans the project, the black box, a backup and an export.
- Tampering with the black box shows: every record carries the hash of the one before it (and a checksum), a signed head ties the chain to a key of yours, and AES-GCM refuses a changed sealed record.

**Not protected**
- **Anyone who has the passphrase, or the unlocked process, sees everything.** While the program runs the key is in memory.
- **The song, lyrics, notes, the history database and the takes** in the project folder are not encrypted (they are the work you share and the files other tools read). Use your system's disk encryption (BitLocker, FileVault, LUKS) for those.
- **Exports** are plain zip files by design: they are meant to be shared. Their black-box segments stay sealed, so the receiver needs the passphrase for those only.
- File names, sizes and the hash of each backed-up file are visible in the backup manifest (the objects are named by the hash of their plain content, so someone who already has a file can tell that it is in the backup).
- The hourly summaries that thinning leaves of old black-box segments (hour, kind, count) are not sealed.
- A signature proves that a key signed a head at some time and that the journal still contains that record unchanged. It does **not** prove when (the time is the signer's clock) or who holds the key.
- Signatures of records that thinning later removed cannot be checked any more (they are listed as "no longer checkable", not as a problem).
- A malicious process running as you can read the vault passphrase from the environment. The vault protects against theft of files, not against malware on a running machine.

## Key loss

- **Lost passphrase and no recovery kit: the sealed black box and the sealed backups cannot be opened by anyone, including us.** There is no back door. The song itself is not sealed and stays usable.
- `ample encrypt enable` is a dangerous act: the dry run says this in plain words, and you must type `encrypt this project` and give a reason.
- `ample encrypt recovery-kit --out file` writes a text file with the **data key itself** and the key settings. Keep it offline (paper or a drive in a drawer). Whoever holds it can open the sealed data without the passphrase, so it is as secret as the passphrase.
- `ample encrypt disable` (also a dangerous act) writes the black box in the clear again. Backups made while it was on stay sealed; their folder keeps a copy of the wrapped key, so the passphrase still opens them.
- A backup folder holds the key of one project: backing up a second project with another key into the same folder is refused (AMP5314).

## How it works

- `.ample/encryption.json` holds the format, the KDF settings, the data key sealed by the passphrase-derived key, and an HMAC key check. It holds no secret by itself.
- A sealed black-box line is `enc1:` plus base64 of nonce, tag and ciphertext of the **plain** line; the hash chain and the checksum are over the plain lines, so verification works the same with or without encryption, and plain and sealed lines may be mixed (the switch is crash-safe). The journal opens the key from the passphrase source (the environment) and refuses to open an encrypted project without it (AMP5310).
- A sealed backup object starts with `AMPLEENC1\n`, then frames of up to 1 MiB, each with its own nonce and tag.
- Signatures are lines of `.ample/signatures.jsonl`: key id, public key, the sequence number and line hash of the signed record, the time and the signature over `ample-sig1|project id|seq|hash|time`. File signatures are `<file>.sig`.
- Key rotation retires the active signing key (its private half is deleted) and makes a new one; retired keys keep verifying.

## Not built (see the plan)

- The operating system credential stores (Windows Credential Manager/DPAPI, Keychain, libsecret) are not used: the vault is a passphrase-sealed file on every system.
- No UI yet for the vault, keys or encryption (the web UI shows nothing of it); everything is on the command line.
- The data key is not rotated (only the passphrase). Re-encrypting with a new key means disabling and enabling again.
- Secrets in a backup bundle (`secrets.enc`), a sibling secrets file and "restore without retyping" from the backup design are not built; backups simply never contain the vault.
- Argon2id and XChaCha20-Poly1305 (see above).
