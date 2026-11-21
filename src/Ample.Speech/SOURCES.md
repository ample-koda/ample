# Sources of Ample.Speech and how to update them

`Ample.Speech` contains the Humanico speech voice and the pronunciation data it reads. What comes from where:

| Part | Origin | Licence | Where it is recorded |
| --- | --- | --- | --- |
| Klatt synthesis (`HumanicoKlatt*.cs`), phone and frame tables, the rate and prosody rules, the calibration (`Data/humanico-calibration.json`) | ported or derived from [Humanico voice](https://github.com/JacobEGarcia/humanico-voice) at revision `ce793bc9304cf34637199b0efde72c8afe130b1d` (`tts.py`, `phones.py`, `klatt80.py`, 10 kHz) | MIT, copyright 2026 Jacob Garcia | `LICENSES/humanico-voice.txt`, `Data/humanico-LICENSE.txt` (copied next to the assembly), `THIRD-PARTY-NOTICES.md`, `README.md` |
| Pronunciation dictionary (`Data/cmudict.dict`, embedded in the assembly) | CMU Pronouncing Dictionary as used by the upstream front end | BSD-2-Clause, copyright 1993-2015 Carnegie Mellon University | `LICENSES/cmudict.txt`, `Data/cmudict-LICENSE.txt`, `THIRD-PARTY-NOTICES.md`, `README.md` |
| Everything else (planner, word and note assignment, retro engine, resource limits, the AMPLE block) | written for AMPLE | MIT (the repository licence) | `LICENSE` |

Pinned identities (a change of any of them is a new renderer identity and invalidates cached takes):

- Dictionary: `HumanicoPronunciationLexicon.SourceIdentity` names the SHA-256 of `Data/cmudict.dict`. The loader refuses any other bytes (`HumanicoCmuDictionary.Parse`). The file is stored with LF line endings (`.gitattributes`), so the hash is the same on Windows and Linux.
- Calibration: `HumanicoCalibratedSegments.CalibrationSha256` is the hash of `Data/humanico-calibration.json` as stored in git (LF). Generators must write LF.
- Reference: `HumanicoReferenceManifest.PinnedFixture` holds the upstream revision, the dictionary hash and the fixed internal rate.

## Updating the pinned source or data

1. Choose the new upstream revision (or dictionary file). Read its LICENSE: if the licence or the copyright line changed, update `LICENSES/humanico-voice.txt`, `Data/humanico-LICENSE.txt`, `THIRD-PARTY-NOTICES.md` and `README.md` in the same change.
2. Dictionary: replace `Data/cmudict.dict` (LF), compute its SHA-256 (`sha256sum`), then change the hash in `HumanicoPronunciationLexicon.SourceIdentity`, `HumanicoReferenceManifest` and the tests that name it; the date in the identity is the date of the update.
3. Code or calibration: regenerate `Data/humanico-calibration.json` with the generator (write LF), update `CalibrationSha256`, and compare against the upstream Python outputs with the staged fixtures. A change that changes samples gets a new `HumanicoIdentity` version and invalidates the cache keys (see plan/speech/PLAN.md, identity section).
4. Run `Ample.Speech.Tests` and the speech renderer tests, listen to a short comparison (same text and seed, old and new) and record the audible change in `plan/findings.md`.
5. Run `node tools/vectors-sync.mjs` if anything under `src/Ample.Audio/Engine` changed.
