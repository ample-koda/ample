// Keeps web/lib/dsp-vectors.json in step with the server engine (local-preview 6.5, owner decision 2026-11-12): hashes the engine sources, and when the hash differs from the one stored in the
// vectors, regenerates them with the CLI and prints the deviations per block. Run before the audio tests (npm run test:audio:*) so a deliberate engine change never needs a manual step.
//   node tools/vectors-sync.mjs            regenerate when stale, report
//   node tools/vectors-sync.mjs --check    exit 1 when stale (no regeneration)
import { createHash } from 'node:crypto';
import { readdirSync, readFileSync, writeFileSync, existsSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('..', import.meta.url)), target = root + 'web/lib/dsp-vectors.json', dir = root + 'src/Ample.Audio/Engine/';
export function engineHash() {
  const h = createHash('sha256');
  for (const f of readdirSync(dir).filter((n) => n.endsWith('.cs')).sort()) h.update(f).update(readFileSync(dir + f));
  return h.digest('hex').slice(0, 16);
}
import { resolve } from 'node:path';
if (!process.argv[1] || resolve(process.argv[1]) !== resolve(fileURLToPath(import.meta.url))) { /* imported for engineHash only */ } else {
const now = engineHash(), stored = existsSync(target) ? JSON.parse(readFileSync(target, 'utf8')) : null;
if (stored?.engineHash === now) { console.log(`dsp vectors current (engine ${now})`); process.exit(0); }
if (process.argv.includes('--check')) { console.error(`dsp vectors are stale (engine ${now}, vectors ${stored?.engineHash ?? 'none'}): run node tools/vectors-sync.mjs`); process.exit(1); }
console.log(`engine changed (${stored?.engineHash ?? 'none'} -> ${now}): regenerating the vectors`);
execFileSync('node', [root + 'tools/gen-dsp-vectors.mjs'], { stdio: 'inherit' });
const fresh = JSON.parse(readFileSync(target, 'utf8')); fresh.engineHash = now; writeFileSync(target, JSON.stringify(fresh));
execFileSync('node', [root + 'tools/dsp-deviations.mjs', '--write'], { stdio: 'inherit' });
console.log('vectors regenerated: review the table above and docs/clientside-preview/conformance.md, then commit.');
}
