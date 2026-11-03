# 06 Seeds and determinism

The explanations live in determinism, controllability and regeneration safety; this page states what the **language** says.

## Master seed
`composition "name" { ... master-seed N }` sets the master seed (unsigned 64-bit integer 0 to 18446744073709551615). Explicit block seeds use the same range; `derived` follows AMPLE's stable generator. Every block with random behaviour derives its own seeds from the master seed and a stable path (block identity and purpose), so a song is a pure function of its text and its recorded takes. The derivation uses the stable integer-only generator; its algorithm and test vectors will be added here and are frozen once released (`rngVersion`).

## Seed parameters
A block may set `seed` or `lm_seed` to `derived` (default) or to a number. A number is a **custom seed**: allowed, recorded, and reported as lint L009 (info). Changing a seed later means takes made with the old one cannot be reproduced from the new one.

## Flags and freeze
| Syntax | Meaning |
| --- | --- |
| `bake` | the rendered take is stored and used instead of rendering again |
| `lock` | the value (for data blocks) is recorded and kept |
| `refresh` | always fetch again (contradicts `lock`: L003) |
| `freeze seeds` | the seeds are fixed; the same inputs and seeds are rendered the same way (when the plugin is deterministic) |
| `freeze plan` | seeds and the melody plan are stored; the voice may be re-rendered |

`bake` is the top of the freeze ladder (free, seeds, plan, take). `freeze` and `bake` are mutually consistent: a baked block needs no freeze word (the serializer omits it).

## What the language does not promise
Regenerating an AI block is only guaranteed to give the same result on the same machine and toolprint. Notices `AMP5101` and `AMP5102` say so ([08](08-diagnostics.md)); the stored take is what guarantees the song.

## Algorithms (RngVersion 1, frozen once released)
- **Seed expansion:** SplitMix64 (state += 0x9E3779B97F4A7C15; z = (z ^ z>>30) * 0xBF58476D1CE4E5B9; z = (z ^ z>>27) * 0x94D049BB133111EB; output z ^ z>>31). A seed is expanded into four 64-bit state words by four calls.
- **Stream:** xoshiro256** (result = rotl(s1 * 5, 7) * 9; t = s1 << 17; s2 ^= s0; s3 ^= s1; s1 ^= s2; s0 ^= s3; s2 ^= t; s3 = rotl(s3, 45)).
- **Bounded integers:** unbiased by rejection: discard values below (2^64 - bound) mod bound, then take the value mod bound. **Unit fractions:** the top 53 bits as an integer divided by 2^53 (the only floating-point step; both numbers are exact). **Shuffle:** Fisher-Yates from the end, j = bounded(i + 1). **Pick:** bounded(count).
- **Seed derivation:** SipHash-2-4 with key (k0 = master seed, k1 = 0x414D504C45534545, the ASCII text "AMPLESEE") over the bytes: the RngVersion byte, then for each path part either 0x01, the UTF-8 length as 4 bytes little endian and the UTF-8 bytes (a string), or 0x02 and the value as 8 bytes little endian (a non-negative integer). The 32-bit seed is the low 32 bits.
- **Verification:** the vectors in `spec-tests/prng/vectors.json` (checked against the published SplitMix64 outputs for seed 1234567 and the SipHash-2-4 paper vector) are tested by the C# code (`Ample.Audio.Random`) and the JavaScript twin (`web/lib/stable-rng.js`). Changing any of this creates a new `RngVersion`; plans store the version they used.

## Guarantee layers
1. A stored take plus the deterministic mix is bit-identical (hash verified).
2. Everything AMPLE renders itself is identical on every supported platform.
3. Regenerating an AI block is guaranteed only locally; the regeneration flow verifies and defaults to restoring the previous take.

## What a change of the master seed reaches
The master seed only changes a block whose seed is **derived** from it. A block does not follow it when it has a custom seed (`seed 77`), when its seed is locked (the take bank keeps the locked value), or when its block type never reads a seed (clips, mixer, master, groups, lyrics, import, effects and the like): the plan of such a block carries seed 0, so it keeps its take when the master seed or the block's name changes. The editor shows the same: after a new master seed only the blocks that follow it, and the blocks that read them, lose their built state (`GET /api/doc/inputs` lists them as `followsMaster`).
