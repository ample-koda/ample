// The "clientPreview" flag of a template says that the browser preview (web/app/clientside-preview.js) renders the block, and an audio
// component made only of such blocks has a browser preview (no AMP2236). The flag was set for 17 effects on 2026-10-29 and then not for the
// effects and instruments the preview learned later (distortion, duck, gate ... and the instruments): this test keeps the two in step.
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../..');
const preview = fs.readFileSync(path.join(root, 'web/app/clientside-preview.js'), 'utf8');
const handled = new Set([...preview.matchAll(/(?:node|block)\.tpl === '([\w-]+)'/g)].map((m) => m[1]));   // block.tpl: the control blocks (controlOf)
const dir = path.join(root, 'spec/templates');
const templates = fs.readdirSync(dir).filter((f) => f.endsWith('.json')).map((f) => JSON.parse(fs.readFileSync(path.join(dir, f), 'utf8')));
const audioIn = (t) => (t.inputs || []).some((p) => p.name === 'in' && p.type === 'audio');
const audioOut = (t) => (t.outputs || []).some((p) => p.type === 'audio');
const instrument = (t) => (t.inputs || []).some((p) => p.name === 'notes' && p.type === 'midi') && (t.outputs || []).some((p) => p.name === 'audio' && p.type === 'audio');

test('every template with clientPreview is rendered by the browser preview', () => {
  const missing = templates.filter((t) => t.clientPreview && !handled.has(t.id)).map((t) => t.id);
  assert.deepEqual(missing, [], `clientPreview is set but clientside-preview.js has no branch for: ${missing.join(', ')}`);
});

test('every effect and instrument the browser preview renders says so with clientPreview', () => {
  const unflagged = templates.filter((t) => handled.has(t.id) && ((audioIn(t) && audioOut(t)) || instrument(t)) && !t.clientPreview).map((t) => t.id);
  assert.deepEqual(unflagged, [], `clientside-preview.js renders these, but their template has no "clientPreview": true: ${unflagged.join(', ')}`);
});

test('distortion and duck have a browser preview (the components of the owner said they had none, 2026-11-17)', () => {
  for (const id of ['distortion', 'duck']) assert.equal(templates.find((t) => t.id === id)?.clientPreview, true, id);
});
