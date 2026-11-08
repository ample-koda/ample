#!/usr/bin/env node
// Issue sync between the public repository and the private one, in two directions. It runs in the PUBLIC repository (workflow issue-sync.yml): free minutes there.
//
//   public -> private   every public issue is mirrored as an issue of its own in the private repository (title, text, labels, state, every comment, with the author named).
//                       Nothing flows back unless the private issue was made from a private one (see below): reply in the public issue itself.
//   private -> public   only a private issue that carries the label `public` is published (as an issue made by the sync account). Its title and text are scanned first;
//                       a hit blocks it (label `public-blocked` and a private comment with rule names and line numbers, never the text). A private comment that starts with
//                       `/public ` is posted as a comment too, after the same scan. Public comments on such an issue come back as comments of the private issue.
//
// Every run reconciles the whole state (the events of the workflow only say "something changed"), so a missed event is caught by the next run, and nothing of an event
// (a title, a text) is ever put in a command line. The numbers of the other side live in hidden markers: <!-- sync:pub#12 h:abc --> in the private mirror of public issue
// 12, <!-- sync:priv#7 h:abc --> in the public issue made from private issue 7, <!-- sync:c#99 h:abc --> in a mirrored comment (99 is the id of the source comment).
// What it needs (environment): SYNC_TOKEN (issues read/write on both repositories), SYNC_PRIVATE_REPO (owner/name), SYNC_SCAN_RULES (JSON, the rules of the private-to-public
// scan: the private side is never published without it), SYNC_BOT_LOGIN (the login of the sync account, so that it does not sync its own writing), GITHUB_REPOSITORY (the public repo).
// Usage: node tools/issue-sync/sync.mjs [--dry-run]

import crypto from 'node:crypto';
import { pathToFileURL } from 'node:url';

export const PUBLISH = 'public', MIRROR = 'from-public', BLOCKED = 'public-blocked';
const INTERNAL = new Set([PUBLISH, MIRROR, BLOCKED]);

const hash = (value) => crypto.createHash('sha1').update(JSON.stringify(value)).digest('hex').slice(0, 10);
const marker = (kind, id, h) => `<!-- sync:${kind}#${id}${h ? ` h:${h}` : ''} -->`;
const READ = (kind) => new RegExp(`<!-- sync:${kind}#(\\d+)(?: h:([0-9a-f]+))? -->`);
export const readMarker = (text, kind) => { const m = READ(kind).exec(text ?? ''); return m ? { id: Number(m[1]), hash: m[2] ?? '' } : null; };
const stripMarkers = (text) => String(text ?? '').replace(/\n*<!-- sync:[^>]*-->/g, '').trimEnd();
const neutral = (text) => String(text ?? '').replace(/@(?=[A-Za-z0-9])/g, '@​');   // a mention in mirrored text notifies nobody
const quote = (text) => String(text ?? '').split(/\r?\n/).map((l) => `> ${l}`).join('\n');
const labelsOf = (issue) => (issue.labels ?? []).map((l) => (typeof l === 'string' ? l : l.name));
const isIssue = (issue) => !issue.pull_request;

/** The rules of the scan from their JSON form: [{name, pattern, flags}]. */
export const compileRules = (json) => (json ? JSON.parse(json) : []).map((r) => ({ name: r.name, pattern: new RegExp(r.pattern, r.flags ?? '') }));

/** Hits of the scan in a text: [{ rule, line }] (the matching text is never kept). Images of a private issue cannot be seen publicly: that is a hit too. */
export function scanText(text, rules) {
  const hits = [];
  const lines = String(text ?? '').split(/\r?\n/);
  lines.forEach((line, i) => {
    for (const r of rules) if (r.pattern.test(line)) hits.push({ rule: r.name, line: i + 1 });
    if (/!\[[^\]]*\]\(https:\/\/(github\.com\/user-attachments|private-user-images\.githubusercontent\.com)/.test(line) || /<img [^>]*src="https:\/\/(github\.com\/user-attachments|private-user-images)/.test(line)) hits.push({ rule: 'private-image', line: i + 1 });
  });
  return hits;
}

/** One run. `api` is the adapter (GitHub in production, a fake in the tests); `log` gets one line for each thing done, never a text. Returns the counts. */
export async function reconcile(api, { pub, priv, bot, rules = [], dryRun = false, log = () => {} }) {
  const count = { created: 0, updated: 0, comments: 0, blocked: 0 };
  const side = (repo) => (repo === pub ? 'public' : 'private');   // the log is public: never the name of the private repository
  const act = async (what, fn) => { log(`${dryRun ? '[dry-run] ' : ''}${what}`); if (!dryRun) await fn(); };
  const pubIssues = (await api.issues(pub)).filter(isIssue);
  const privIssues = (await api.issues(priv)).filter(isIssue);
  const mirrorOf = new Map(), madeFrom = new Map();   // public number -> private mirror; private number -> public issue made from it
  for (const q of privIssues) { const m = readMarker(q.body, 'pub'); if (m) mirrorOf.set(m.id, q); }
  for (const p of pubIssues) { const m = readMarker(p.body, 'priv'); if (m) madeFrom.set(m.id, p); }

  /** Comments of one issue become comments of another (the author named, mentions silenced); `filter` says which ones; `format` makes the text. */
  async function mirrorComments(srcRepo, src, dstRepo, dst, { take, format }) {
    const from = await api.comments(srcRepo, src.number), have = await api.comments(dstRepo, dst.number);
    const seen = new Map();
    for (const c of have) { const m = readMarker(c.body, 'c'); if (m) seen.set(m.id, { c, hash: m.hash }); }
    for (const c of from) {
      if (c.user?.login === bot || !take(c)) continue;
      const text = format(c), h = hash(text), old = seen.get(c.id);
      const body = `${text}\n\n${marker('c', c.id, h)}`;
      if (!old) { await act(`comment ${side(srcRepo)}#${src.number}/${c.id} -> ${side(dstRepo)}#${dst.number}`, () => api.createComment(dstRepo, dst.number, body)); count.comments++; }
      else if (old.hash !== h) { await act(`edit comment ${side(dstRepo)}#${dst.number}/${old.c.id}`, () => api.updateComment(dstRepo, old.c.id, body)); count.comments++; }
    }
  }

  // ---- public -> private ----
  for (const p of pubIssues) {
    const made = readMarker(p.body, 'priv');
    if (made) {   // an issue that the sync made from a private one: only what people write there comes back
      const q = privIssues.find((x) => x.number === made.id);
      if (q) await mirrorComments(pub, p, priv, q, { take: () => true, format: (c) => `**${neutral(c.user?.login ?? 'someone')}** wrote in the public issue:\n\n${quote(neutral(c.body))}` });
      continue;
    }
    if (p.user?.login === bot) continue;
    const state = { title: p.title, body: p.body ?? '', labels: labelsOf(p).sort(), state: p.state };
    const h = hash(state), mirror = mirrorOf.get(p.number);
    const text = `Public issue by **${neutral(p.user?.login ?? 'someone')}**: ${p.html_url ?? `#${p.number}`}\n\n${quote(neutral(p.body))}\n\n${marker('pub', p.number, h)}`;
    let target = mirror;
    if (!mirror) {
      await act(`create private mirror of public #${p.number}`, async () => { target = await api.createIssue(priv, { title: `[public #${p.number}] ${p.title}`, body: text, labels: [...labelsOf(p), MIRROR] }); if (p.state === 'closed') await api.updateIssue(priv, target.number, { state: 'closed' }); });
      count.created++;
    } else if (readMarker(mirror.body, 'pub')?.hash !== h) {
      const labels = [...new Set([...labelsOf(mirror), ...labelsOf(p), MIRROR])];
      await act(`update private mirror #${mirror.number} of public #${p.number}`, () => api.updateIssue(priv, mirror.number, { title: `[public #${p.number}] ${p.title}`, body: text, labels, state: p.state }));
      count.updated++;
    }
    if (target) await mirrorComments(pub, p, priv, target, { take: () => true, format: (c) => `**${neutral(c.user?.login ?? 'someone')}** wrote:\n\n${quote(neutral(c.body))}` });
  }

  // ---- private -> public: only what carries the label ----
  for (const q of privIssues) {
    const labels = labelsOf(q);
    if (!labels.includes(PUBLISH) || labels.includes(MIRROR) || readMarker(q.body, 'pub')) continue;
    if (!rules.length) { log('no scan rules (SYNC_SCAN_RULES): nothing is published'); break; }
    const body = stripMarkers(q.body), existing = madeFrom.get(q.number);
    const hits = [...scanText(q.title, rules).map((h) => ({ ...h, line: 'title' })), ...scanText(body, rules)];
    if (hits.length) {
      if (!labels.includes(BLOCKED)) {
        const list = [...new Set(hits.map((h) => `${h.rule} (${h.line === 'title' ? 'title' : `line ${h.line}`})`))].join(', ');
        await act(`block private #${q.number}: ${hits.length} hit(s)`, async () => { await api.updateIssue(priv, q.number, { labels: [...labels, BLOCKED] }); await api.createComment(priv, q.number, `Not published: the scan found ${hits.length} hit(s): ${list}. Fix the title or the text, then it is tried again on the next run.`); });
        count.blocked++;
      }
      continue;
    }
    const publicLabels = labels.filter((l) => !INTERNAL.has(l));
    const h = hash({ title: q.title, body, labels: publicLabels.sort(), state: q.state });
    const text = `${body}\n\n${marker('priv', q.number, h)}`;
    let target = existing;
    if (!existing) {
      await act(`publish private #${q.number}`, async () => {
        target = await api.createIssue(pub, { title: q.title, body: text, labels: publicLabels });
        if (q.state === 'closed') await api.updateIssue(pub, target.number, { state: 'closed' });
        await api.createComment(priv, q.number, `Published as public issue #${target.number}${target.html_url ? `: ${target.html_url}` : ''}. A comment that starts with \`/public \` is posted there too.`);
      });
      count.created++;
    } else if (readMarker(existing.body, 'priv')?.hash !== h) {
      await act(`update public #${existing.number} from private #${q.number}`, () => api.updateIssue(pub, existing.number, { title: q.title, body: text, labels: publicLabels, state: q.state }));
      count.updated++;
    }
    if (labels.includes(BLOCKED)) await act(`unblock private #${q.number}`, () => api.updateIssue(priv, q.number, { labels: labels.filter((l) => l !== BLOCKED) }));
    if (target) {
      // the comments that start with /public are scanned one by one: a hit keeps that comment private
      const ok = (c) => /^\/public\s/.test(c.body ?? '') && scanText(c.body.replace(/^\/public\s+/, ''), rules).length === 0;
      await mirrorComments(priv, q, pub, target, { take: ok, format: (c) => c.body.replace(/^\/public\s+/, '') });
    }
  }
  return count;
}

// ---- GitHub ----
export function githubApi(token, fetchFn = fetch) {
  const call = async (method, path, body) => {
    const response = await fetchFn(`https://api.github.com${path}`, { method, headers: { Authorization: `Bearer ${token}`, Accept: 'application/vnd.github+json', 'X-GitHub-Api-Version': '2022-11-28', 'User-Agent': 'ample-issue-sync', ...(body ? { 'Content-Type': 'application/json' } : {}) }, body: body ? JSON.stringify(body) : undefined });
    if (!response.ok) throw new Error(`${method} ${path.replace(/\?.*/, '')}: ${response.status}`);
    return response.status === 204 ? null : response.json();
  };
  const all = async (path) => { const out = []; for (let page = 1; ; page++) { const part = await call('GET', `${path}${path.includes('?') ? '&' : '?'}per_page=100&page=${page}`); out.push(...part); if (part.length < 100) return out; } };
  return {
    issues: (repo) => all(`/repos/${repo}/issues?state=all`),
    comments: (repo, number) => all(`/repos/${repo}/issues/${number}/comments`),
    createIssue: (repo, data) => call('POST', `/repos/${repo}/issues`, data),
    updateIssue: (repo, number, data) => call('PATCH', `/repos/${repo}/issues/${number}`, data),
    createComment: (repo, number, body) => call('POST', `/repos/${repo}/issues/${number}/comments`, { body }),
    updateComment: (repo, id, body) => call('PATCH', `/repos/${repo}/issues/comments/${id}`, { body }),
    me: async () => (await call('GET', '/user')).login,
  };
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const env = process.env;
  if (!env.SYNC_TOKEN) { console.log('SYNC_TOKEN is not set: the issue sync is not set up here'); process.exit(0); }   // a quiet no-op until the secrets exist
  const need = (name) => { if (!env[name]) { console.error(`${name} is not set`); process.exit(2); } return env[name]; };
  const api = githubApi(need('SYNC_TOKEN'));
  const bot = env.SYNC_BOT_LOGIN || await api.me().catch(() => '');
  if (!bot) { console.error('SYNC_BOT_LOGIN is not set and the token has no user'); process.exit(2); }
  const result = await reconcile(api, { pub: need('GITHUB_REPOSITORY'), priv: need('SYNC_PRIVATE_REPO'), bot, rules: compileRules(env.SYNC_SCAN_RULES), dryRun: process.argv.includes('--dry-run'), log: (line) => console.log(line.replace(/[\r\n]/g, ' ')) });
  console.log(JSON.stringify(result));
}
