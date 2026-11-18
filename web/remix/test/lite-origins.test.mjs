// Remix: the request for the origins of the chains of a song. It asks only when there is a chain, and a server that cannot answer leaves the marks as they were.
import test from 'node:test';
import assert from 'node:assert/strict';
import { fetchOrigins } from '../lite-origins.mjs';

const CHAIN = 'midi-clip theme {\n  melody "c4:1 d4:1"\n}\nmidi-clip high at 1 for auto {\n  notes theme.notes |> transpose(+12)\n}\n';
const PLAIN = 'midi-clip a at 1 for 1 {\n  notes 1:0:1:60:100\n}\n';

test('origins: a song with no chain asks nothing', async () => {
  let asked = 0;
  assert.deepEqual(await fetchOrigins({ text: PLAIN, files: [], post: async () => { asked++; return { origins: { a: [1] } }; } }), {});
  assert.equal(asked, 0);
});

test('origins: the chain clips and the text files go to the server, audio files do not', async () => {
  let sent = null;
  const answer = { high: [{ at: 0, len: 1, pitch: 72, source: 'theme', index: 0, lap: 0 }] };
  const files = [{ path: 'song.amplet', text: CHAIN }, { path: 'kick.wav', blob: 1 }];
  const got = await fetchOrigins({ text: CHAIN, files, post: async (path, body) => { sent = { path, body }; return { origins: answer }; } });
  assert.deepEqual(got, answer);
  assert.equal(sent.path, '/api/remix/notes/origins');
  assert.deepEqual(sent.body.blocks, ['high'], 'only the clip that is a chain');
  assert.deepEqual(sent.body.files.map((f) => f.path), ['song.amplet']);
});

test('origins: a server that fails or answers nonsense gives {} and never throws', async () => {
  assert.deepEqual(await fetchOrigins({ text: CHAIN, files: [], post: async () => { throw new Error('429'); } }), {});
  assert.deepEqual(await fetchOrigins({ text: CHAIN, files: [], post: async () => ({ origins: null }) }), {});
});
