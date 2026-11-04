import test from 'node:test';
import assert from 'node:assert/strict';
import { engineProblem, formatEngine, parseEngine } from '../engine-choice.mjs';

test('an engine text is split into its place and its model, with or without quotes', () => {
  assert.deepEqual(parseEngine('"faer:xl-sft"'), { route: 'faer', model: 'xl-sft' });
  assert.deepEqual(parseEngine('local'), { route: 'local', model: '' });
  assert.deepEqual(parseEngine(' faer-else-local : acestep-v15-turbo.gguf '), { route: 'faer-else-local', model: 'acestep-v15-turbo.gguf' });
  assert.deepEqual(parseEngine(''), { route: '', model: '' });
  assert.deepEqual(parseEngine(undefined), { route: '', model: '' });
});

test('the text to write is empty for the project default and has a model only after a place', () => {
  assert.equal(formatEngine('', ''), '');
  assert.equal(formatEngine('', 'turbo'), '');
  assert.equal(formatEngine('faer', ''), 'faer');
  assert.equal(formatEngine('local', 'turbo'), 'local:turbo');
});

const connected = { faer: { configured: true, connected: true, status: { capabilities: { song: { ready: true }, 'voice-ace': { ready: false } } } }, models: [{ file: 'a-sft.gguf', local: true, faer: false }, { file: 'b-turbo.gguf', local: false, faer: true }] };

test('what would stop the build is said beforehand', () => {
  assert.equal(engineProblem({ blockType: 'ace-music', route: '', model: '', state: connected }), '');
  assert.equal(engineProblem({ blockType: 'ace-music', route: 'faer', model: 'xl-sft', state: connected }), '');
  assert.match(engineProblem({ blockType: 'ace-music', route: 'faer', model: '', state: { faer: { configured: false }, models: [] } }), /AMP5380/);
  assert.match(engineProblem({ blockType: 'ace-music', route: 'faer', model: '', state: { faer: { configured: true, connected: false }, models: [] } }), /not reachable/);
  assert.match(engineProblem({ blockType: 'ai-voice', route: 'faer', model: '', state: connected }), /not ready/);
  assert.match(engineProblem({ blockType: 'ace-music', route: 'local', model: 'b-turbo.gguf', state: connected }), /AMP5382/);
  assert.match(engineProblem({ blockType: 'ace-music', route: 'faer', model: 'a-sft.gguf', state: connected }), /not on the Faer server/);
  assert.match(engineProblem({ blockType: 'ace-music', route: 'local', model: 'nope.gguf', state: connected }), /Neither server/);
  assert.equal(engineProblem({ blockType: 'ace-music', route: 'local', model: 'a-sft.gguf', state: connected }), '');
  assert.equal(engineProblem({ blockType: 'ace-music', route: 'faer-else-local', model: '', state: { faer: { configured: false }, models: [] } }), '');
});
