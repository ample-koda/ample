// Tests of the issue sync with an in-memory GitHub: no network. node --test tools/issue-sync/sync.test.mjs
import test from 'node:test';
import assert from 'node:assert/strict';
import { BLOCKED, MIRROR, PUBLISH, compileRules, reconcile, scanText } from './sync.mjs';

const PUB = 'org/pub', PRIV = 'org/priv', BOT = 'sync-bot';
function fake() {
  const repos = { [PUB]: { issues: [], comments: new Map() }, [PRIV]: { issues: [], comments: new Map() } };
  let seq = 100;
  const api = {
    issues: async (r) => repos[r].issues.map((i) => ({ ...i })),
    comments: async (r, n) => (repos[r].comments.get(n) ?? []).map((c) => ({ ...c })),
    createIssue: async (r, d) => { const issue = { number: repos[r].issues.length + 1, state: 'open', user: { login: BOT }, html_url: `https://x/${r}/${repos[r].issues.length + 1}`, ...d }; repos[r].issues.push(issue); return issue; },
    updateIssue: async (r, n, d) => { Object.assign(repos[r].issues.find((i) => i.number === n), d); },
    createComment: async (r, n, body) => { const list = repos[r].comments.get(n) ?? []; list.push({ id: ++seq, body, user: { login: BOT } }); repos[r].comments.set(n, list); },
    updateComment: async (r, id, body) => { for (const list of repos[r].comments.values()) { const c = list.find((x) => x.id === id); if (c) c.body = body; } },
  };
  const add = (r, issue) => { const i = { number: repos[r].issues.length + 1, state: 'open', labels: [], user: { login: 'alice' }, body: '', ...issue }; repos[r].issues.push(i); return i; };
  const comment = (r, n, body, login = 'alice') => { const list = repos[r].comments.get(n) ?? []; list.push({ id: ++seq, body, user: { login } }); repos[r].comments.set(n, list); };
  return { repos, api, add, comment };
}
const rules = compileRules(JSON.stringify([{ name: 'secret-word', pattern: 'hunter2', flags: 'i' }]));
const run = (g, extra = {}) => reconcile(g.api, { pub: PUB, priv: PRIV, bot: BOT, rules, ...extra });

test('a public issue is mirrored once, with its comments, and nothing is doubled by a second run', async () => {
  const g = fake();
  g.add(PUB, { title: 'Crash on open', body: 'It crashes @bob', labels: ['bug'] });
  g.comment(PUB, 1, 'Same here');
  const first = await run(g);
  assert.deepEqual([first.created, first.comments], [1, 1]);
  const m = g.repos[PRIV].issues[0];
  assert.match(m.title, /^\[public #1\] Crash on open/);
  assert.ok(m.labels.includes('bug') && m.labels.includes(MIRROR));
  assert.ok(!m.body.includes('@bob') && m.body.includes('@​bob'));
  const second = await run(g);
  assert.deepEqual(second, { created: 0, updated: 0, comments: 0, blocked: 0 });
});

test('an edit and a close of the public issue reach the mirror, an edited comment too', async () => {
  const g = fake();
  const p = g.add(PUB, { title: 'A', body: 'one' });
  g.comment(PUB, 1, 'first');
  await run(g);
  p.body = 'two'; p.state = 'closed'; g.repos[PUB].comments.get(1)[0].body = 'edited';
  const r = await run(g);
  assert.equal(r.updated, 1); assert.equal(r.comments, 1);
  assert.equal(g.repos[PRIV].issues[0].state, 'closed');
  assert.match(g.repos[PRIV].issues[0].body, /two/);
  assert.match(g.repos[PRIV].comments.get(1)[0].body, /edited/);
});

test('closing the private mirror by hand is not undone while the public issue is unchanged', async () => {
  const g = fake();
  g.add(PUB, { title: 'A' });
  await run(g);
  g.repos[PRIV].issues[0].state = 'closed';
  await run(g);
  assert.equal(g.repos[PRIV].issues[0].state, 'closed');
});

test('a private issue without the label stays private; with it, it is published once', async () => {
  const g = fake();
  g.add(PRIV, { title: 'Internal plan', body: 'secret stuff' });
  g.add(PRIV, { title: 'Please add X', body: 'details', labels: [PUBLISH, 'enhancement'] });
  const first = await run(g);
  assert.equal(first.created, 1);
  assert.equal(g.repos[PUB].issues.length, 1);
  const pub = g.repos[PUB].issues[0];
  assert.equal(pub.title, 'Please add X');
  assert.deepEqual(pub.labels, ['enhancement']);
  assert.match(g.repos[PRIV].comments.get(2)[0].body, /Published as public issue #1/);
  assert.deepEqual(await run(g), { created: 0, updated: 0, comments: 0, blocked: 0 });
});

test('a hit of the scan blocks the publication and the private comment names the rule, not the text', async () => {
  const g = fake();
  g.add(PRIV, { title: 'Bug', body: 'line one\nthe password is hunter2', labels: [PUBLISH] });
  const r = await run(g);
  assert.equal(r.blocked, 1);
  assert.equal(g.repos[PUB].issues.length, 0);
  const note = g.repos[PRIV].comments.get(1)[0].body;
  assert.match(note, /secret-word \(line 2\)/);
  assert.ok(!note.includes('hunter2'));
  assert.ok(g.repos[PRIV].issues[0].labels.includes(BLOCKED));
  assert.equal((await run(g)).blocked, 0);   // not repeated
  g.repos[PRIV].issues[0].body = 'fixed text';
  assert.equal((await run(g)).created, 1);   // fixed: published, and the block label is taken off
  assert.ok(!g.repos[PRIV].issues[0].labels.includes(BLOCKED));
});

test('without scan rules nothing is published (fail closed)', async () => {
  const g = fake();
  g.add(PRIV, { title: 'Ok', labels: [PUBLISH] });
  await reconcile(g.api, { pub: PUB, priv: PRIV, bot: BOT, rules: [] });
  assert.equal(g.repos[PUB].issues.length, 0);
});

test('a private image blocks, a /public comment goes public after its own scan, other private comments never do', async () => {
  const g = fake();
  g.add(PRIV, { title: 'Img', body: '![x](https://github.com/user-attachments/assets/abc)', labels: [PUBLISH] });
  assert.equal((await run(g)).blocked, 1);
  const h = fake();
  h.add(PRIV, { title: 'Talk', body: 'hello', labels: [PUBLISH] });
  h.comment(PRIV, 1, 'internal remark');
  h.comment(PRIV, 1, '/public thanks for the report');
  h.comment(PRIV, 1, '/public my password hunter2');
  await run(h);
  const posted = (h.repos[PUB].comments.get(1) ?? []).map((c) => c.body);
  assert.equal(posted.length, 1);
  assert.match(posted[0], /^thanks for the report/);
});

test('public replies on a published issue come back to the private issue; the sync does not mirror its own issue as a new one', async () => {
  const g = fake();
  g.add(PRIV, { title: 'Talk', body: 'hello', labels: [PUBLISH] });
  await run(g);
  g.comment(PUB, 1, 'I can reproduce this', 'carol');
  const r = await run(g);
  assert.equal(g.repos[PRIV].issues.length, 1);   // no mirror of the published issue
  assert.equal(r.comments, 1);
  assert.match(g.repos[PRIV].comments.get(1).at(-1).body, /carol/);
});

test('the log of a run never holds a text or the name of a repository', async () => {
  const g = fake();
  g.add(PUB, { title: 'Secret title', body: 'Secret body' });
  const lines = [];
  await run(g, { log: (l) => lines.push(l) });
  for (const l of lines) { assert.ok(!/Secret|priv\b|org\/priv/.test(l), l); }
});

test('scanText reports rule and line only', () => {
  assert.deepEqual(scanText('a\nb hunter2', rules), [{ rule: 'secret-word', line: 2 }]);
});
