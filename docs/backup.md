> Status: the backup store, the archive (export and import), restore, prune, the CLI and API, and encrypted backups are built; the backup screens are not (see "As built" at the end). The rest of this page is the design the feature started from (profiles, release bundles); where the built version differs, "As built" says so.

# Backup and restore

`ample backup` produces one zip. `ample restore` puts it back into a **new folder** (never overwrites). The sections before "As built" are design notes; "As built" says what exists.

## What is inside
```
my-song-backup-2026-10-02T1015.zip
├─ BACKUP-MANIFEST.json        # backup id, ample version, profile, time, machine; sha256 per file; one root hash
├─ RESTORE.md                  # plain-language instructions (readable without AMPLE)
├─ project/                    # sources (small)
│  ├─ song.amplet  ample.lock  rules/  cuts/  credits/  lyrics/  chords/  midi/
│  └─ .ample-project.json      # name, master seed, format version
├─ store/
│  ├─ history.db               # consistent copy of the SQLite history (VACUUM INTO)
│  └─ cas/ab/cd/<hash>...      # frozen audio and stems by hash (the big things)
├─ releases/   v1.release  v1.1-tweak.release  club-remix.release  [v1.bundle if the git mirror is on]
├─ snapshots/  v1.snapshot     # full inventory with all hashes and toolprints
├─ ledger/     ledger.jsonl    # hash chain of releases (+ last hash stored separately)
├─ takes/      bank.json  pinned/...
├─ tools/      registry/ (manifests, toolprints, certificates, verification history)  store/ (full profile, license permitting)
├─ packs/                      # soundsets (full profile, with license info)
├─ exports/    v1/ master.flac  stems/
├─ external/   copied/...  MISSING.txt
├─ secrets/                    # only when chosen, see below
│  ├─ secrets.enc              # encrypted bundle of the selected secrets
│  └─ SECRETS-README.txt       # what this is; there is NO password recovery
└─ meta/
   ├─ environment.json         # OS, GPU, driver, ample version
   ├─ models.lock              # hashes of used models (the models themselves are not included)
   ├─ secrets-index.json       # names and where they are used (never values)
   └─ EXCLUDED.txt             # what was left out on purpose, and why
```

## Profiles
| Profile | Contents | Rough size |
|---|---|---|
| `minimal` | sources, lock, snapshots, ledger, take metadata | KB to a few MB |
| `standard` | + pinned takes, releases, exports | tens of MB |
| `complete` | + all takes, tool binaries, packs, copied external files | GB |

Choose what to export in a checklist (also saved as a named profile): sources, takes, releases, assets, tools, packs, credits with contact details, and **each secret separately**.

## Secrets
Secrets live in a vault outside the project (OS key store; on Linux an encrypted file). The project refers to them by name (`secret:weather_api`). Only secrets you **declare** count (tokens, passwords, private keys, URLs with tokens), never your whole environment.

| Choice | Result |
|---|---|
| **Secrets inside** (suggested default) | `secrets/secrets.enc` in the zip, encrypted with your master passphrase. The rest may stay readable, or everything can be encrypted (`--encrypt all`). |
| **Secrets in a sibling file** | `my-song-backup-....secrets.enc` next to the zip, same backup id. Store it elsewhere (password manager, other disk). |
| **No secrets** | names and usage only, no values |

The release signing key is **off by default** and must be ticked on purpose, with a warning: whoever holds it can sign in your name.

### Encryption
- Envelope: a random data key encrypts the secrets (AES-256-GCM or XChaCha20-Poly1305, authenticated); that key is wrapped with a key derived from the passphrase (memory-hard, e.g. Argon2id, parameters stored in the header).
- **No password recovery.** Lose the passphrase and the secrets are gone. Optional **recovery key**: a random key shown once (words or QR) and stored offline.
- The manifest hashes the **ciphertext**, never a plaintext secret (a hash of a weak secret can be brute-forced).
- Prefer an existing, reviewed format (the **age** format, passphrase or public keys) over a home-made one, if a good library exists.
- Honest trade-off: secrets inside the same file as the data is convenient, but whoever has the zip and the passphrase has everything. The sibling file splits that knowledge. When "inside" is chosen the tool prints one line: "Secrets are in this file, encrypted. Keep them separate instead?"

## Restore without retyping
```
$ ample restore my-song-backup.zip
  Passphrase: ****     -> 4 secrets restored   tested: weather_api ok, remote_token ok
  Missing (not in the backup): ftp_pw    -> [fill in now] [later]
```
- One passphrase, then all secrets go back into the local vault automatically (re-protected for the new machine; machine-bound secrets cannot be copied raw, which is why they are exported into the encrypted bundle).
- Anything missing appears in **one** screen: the name, where it is used, and a **test** button.
- If the sibling file is absent, everything else is restored and only that list is asked for.

## Checks
- `ample backup verify file.zip` compares every file with the manifest and the root hash without unpacking into a project.
- Reproducible zip: sorted entries, fixed timestamps, so identical content gives an identical zip hash.
- Audio (FLAC) is stored without extra compression; above 4 GB use Zip64 or a split backup.
- `ample restore --verify` checks all hashes after restoring and reports what is missing or not reproducible (frozen-only without a copy).

## Not included (also listed in `meta/EXCLUDED.txt`)
Caches and temp files, logs, big models (hash only), drivers and system files (recorded as environment), and by default the full take bank in `standard`.

## As built
**Where:** `Ample.Core/Backup` (`BackupStore`, `Archive`, `PruneBackupsAct`, `RestoreInPlaceAct`, `BackupPolicy`), the CLI `ample backup create|list|verify|prune|policy`, `ample export`, `ample import`, `ample restore`, and the API routes `/api/backup*`, `/api/export`, `/api/acts/prune-backups`, `/api/acts/restore-in-place` (the health view also shows the last backup).

### The backup store (a folder you choose)
```
<target>/objects/ab/abcdef...     one file per distinct content (name = sha256), written atomically (.part then rename)
<target>/backups/<id>.json        one manifest per backup, immutable, written last
<target>/verified.json            which backups passed a verify (the "last good" record)
```
- **Incremental by content:** a second backup stores only the files whose content is new; an unchanged project stores nothing (0 new files in the tests).
- **What goes in:** every file of the project except `.ample/locks`, `.ample/tmp`, `.ample/backups`, `.ample/vault*`, `*.secret`, `*.tmp` and the SQLite journals. `history.db` is copied with the SQLite backup API, so the copy is consistent while the project is open. `--no-takes` leaves out `.ample/takes` and `.ample/data`.
- **Manifest** (format 1): `id` (time and the start of the entry hash), project id and name, time, whether takes are included, and per file path, hash and size.
- **Resumable:** an interrupted backup leaves only objects (and `.part` files); the next run reuses them and writes the manifest. Left-over temp folders are cleaned after an hour.
- **Space:** the free space of the target is checked before anything is written (AMP5300).
- **Verify** rehashes every stored file (missing and damaged ones are listed) and, when clean, records the backup as the last good one. **Restore** copies each file through a hash check and refuses damaged data; it goes into a new folder (AMP5301 if it is not empty).
- **Prune** (`--keep N`, `--daily D`) is a dangerous act with a dry run; the newest backup and the last good one are always kept; files that no kept backup uses are removed.
- **In-place restore** is a dangerous act: files are replaced or added, files that are not in the backup are moved to `.ample/tmp/restore-trash-*` (never deleted), the history comes back as it was. Close other programs that use the project first.
- **Before dangerous acts:** `ample backup policy --target <folder> --auto on` (or `/api/backup/policy`) makes a backup before every dangerous act the server runs; a failing backup stops the act (AMP5305). The dry run of every act can tell how old the last backup is (`BackupPolicy.Suggestion`).

### The archive (export and import)
One zip file (any zip tool reads it): `manifest.json` and `files/<path>`. The manifest is `{ "format": 1, "tool": "ample", "projectId", "projectName", "created", "includesTakes", "entries": [ { "path", "hash": "sha256:...", "size" } ] }`. `ample import` checks every hash while extracting into a new folder (AMP5302, and nothing is left behind), refuses a folder that is not empty, rejects paths that leave the folder, and with `--new-id` gives the project a new id (the default keeps the id). Secrets and locks are never exported.

### Measured (2026-10-03, Windows 11, Debug build, one machine)
A project of 40 MB of audio and a few small files: create 209 ms (191 MB/s), verify 193 ms, restore 208 ms.

### Not built (and where)
Backup profiles (minimal, standard, complete) and the checklist of what to include (only takes in or out), secrets bundles, release bundles and the git mirror, remote targets, scheduled backups (only "before a dangerous act"), point-in-time restore from AMPLE Redux pre-images, `RESTORE.md` inside the backup, and the backup screens.

## Encrypted projects (as built)
When a project is encrypted ([Security](security.md)), every object a backup stores is sealed (AES-256-GCM, frames of 1 MiB) and the backup folder gets a copy of the wrapped key as `encryption.json` (never the passphrase); verify and restore open the objects with the passphrase from `AMPLE_PASSPHRASE`. A folder that holds the key of one project refuses another project's key (AMP5314). Exports stay plain zip files; only their black-box segments stay sealed. Backups never contain the vault (`vault.json` lives in the library folder, outside every project).
