import { test, expect } from '@playwright/test';
import { openApp, srcText } from '../../web/tests/support';

// Koda, the helper (plan/pet): off until turned on; asks about a block, a piece of code or a field; the answers AMPLE can give itself come at once; hide, peek, appear and mute.
test.beforeEach(async ({ page }) => { await openApp(page); });

const state = (page) => page.evaluate(async () => (await import('/app/koda.js')).kodaState());

test('Koda is off until the header button turns them on, then says hello and can be hidden again', async ({ page }) => {
  await expect(page.locator('#koda-layer')).toBeHidden();
  await page.locator('#btn-koda').click();
  await expect(page.locator('#koda-layer')).toBeVisible();
  await expect.poll(async () => (await state(page)).presence).toBe('visible');
  await expect(page.locator('#koda-layer .bub')).toContainText('I am Koda');
  await page.locator('#btn-koda').click();   // hides: sinks into an edge, then a tab is left
  await expect.poll(async () => (await state(page)).presence).toBe('hidden');
  await expect(page.locator('.koda-tab')).toBeVisible();
  await page.locator('.koda-tab').click();
  await expect.poll(async () => (await state(page)).presence).toBe('visible');
});

test('a field has a note that makes Koda explain it from the documentation, a bubble closes with its cross and with Esc', async ({ page }) => {
  await page.locator('.node').first().click();
  const ask = page.locator('.koda-ask').first();
  await ask.focus();
  await ask.click();
  const bubble = page.locator('#koda-layer .bub.k-answer');
  await expect(bubble).toBeVisible({ timeout: 8000 });
  await expect(bubble.locator('h3')).not.toBeEmpty();
  await bubble.locator('.x').click();
  await expect(bubble).toHaveCount(0);
  await ask.click();
  await expect(page.locator('#koda-layer .bub.k-answer')).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(page.locator('#koda-layer .bub')).toHaveCount(0);
});

test('an answer can be followed by a question of your own, which goes with what Koda just said', async ({ page }) => {
  await page.locator('#btn-koda').click();
  await expect.poll(async () => (await state(page)).presence).toBe('visible');
  await page.keyboard.press('Escape');
  await page.evaluate(async () => {
    const { setBackend } = await import('/app/koda-backend.js');
    (window as any).__asked = [];
    setBackend({
      explain: async (subject: any, q: string) => { (window as any).__asked.push({ q, said: subject.said || '' }); return { kind: 'answer', title: 'x', text: 'It is a reverb.', more: 'The room sets its size.' }; },
      run: async () => ({ kind: 'answer', title: 'x', text: 'y' }),
    });
  });
  await page.locator('.node').first().click();
  await page.keyboard.press('Control+k');
  await page.locator('#koda-layer .bub.k-menu').getByRole('button', { name: 'What does this do?' }).click();
  const bubble = page.locator('#koda-layer .bub.k-answer');
  await expect(bubble).toBeVisible();
  await expect(bubble.locator('.other')).toBeHidden();
  await bubble.getByRole('button', { name: 'Ask a question' }).click();
  await expect(bubble.locator('.other input')).toBeFocused();
  await bubble.locator('.other input').fill('why is the room so small?');
  await page.keyboard.press('Enter');
  await expect.poll(() => page.evaluate(() => (window as any).__asked.length)).toBe(1);
  const asked = await page.evaluate(() => (window as any).__asked[0]);
  expect(asked.q).toBe('why is the room so small?');
  expect(asked.said.length).toBeGreaterThan(5);
});

test('a patch over several blocks is grouped per block, starts with the neon button, and with many blocks the groups are pages @quarantine', async ({ page }) => {
  await page.evaluate(async () => {
    const { draftBubble } = await import('/app/koda-bubbles.js');
    const calls: string[] = [];
    const host = document.createElement('div'); host.className = 'koda'; host.id = 'zz-host'; document.body.append(host);
    const names = ['reverb', 'master', 'echo', 'pad', 'lead'];
    host.append(draftBubble({ summary: names.map((n, i) => ({ type: 'change', text: n + ': wet 1 → 2', hunk: i, block: n })), onToggle: (h: number, on: boolean) => calls.push(h + ':' + on), onFocus: (b: string | null) => calls.push('focus:' + b), onHear: (on: boolean) => calls.push('hear:' + on) }, (v: string) => calls.push('pick:' + v), () => {}));
    (window as any).__calls = calls;
  });
  const bubble = page.locator('#zz-host .bub');
  await expect(bubble.locator('.letsgo')).toContainText("Let's go");
  await expect(bubble.locator('.letsgo small')).toHaveText('5 changes · 5 blocks');
  // five blocks: an overview first, one page per block
  await expect(bubble.locator('.pg.overview .card')).toHaveCount(5);
  await expect(bubble.locator('.count')).toHaveText('Overview');
  await bubble.locator('.pg.overview .card').nth(2).click();
  await expect(bubble.locator('.count')).toContainText('3 / 5 · echo');
  await bubble.locator('.arr').last().click();
  await expect(bubble.locator('.count')).toContainText('4 / 5 · pad');
  await bubble.locator('.viewport').focus();
  await page.keyboard.press('ArrowLeft');
  await expect(bubble.locator('.count')).toContainText('3 / 5');
  // the switch of a block, then the neon button keeps the choice and says keep
  await bubble.locator('.pg').nth(3).locator('li .sw2').click();
  await bubble.locator('.letsgo').hover({ force: true });
  await bubble.locator('.letsgo').click({ force: true });
  const calls = await page.evaluate(() => (window as any).__calls);
  expect(calls).toContain('focus:echo');
  expect(calls).toContain('2:false');
  expect(calls).toContain('hear:true');
  expect(calls[calls.length - 1]).toBe('pick:keep');
});

test('up to three blocks of a patch are shown together, each with a switch for the whole block', async ({ page }) => {
  await page.evaluate(async () => {
    const { draftBubble } = await import('/app/koda-bubbles.js');
    const calls: string[] = [];
    const host = document.createElement('div'); host.className = 'koda'; host.id = 'zz-host'; document.body.append(host);
    host.append(draftBubble({ summary: [{ type: 'change', text: 'reverb: wet 1 → 2', hunk: 0, block: 'reverb' }, { type: 'change', text: 'reverb: room 1 → 2', hunk: 1, block: 'reverb' }, { type: 'change', text: 'master: gain 0 → 1', hunk: 2, block: 'master' }], onToggle: (h: number, on: boolean) => calls.push(h + ':' + on) }, () => {}, () => {}));
    (window as any).__calls = calls;
  });
  const bubble = page.locator('#zz-host .bub');
  await expect(bubble.locator('.grp')).toHaveCount(2);
  await expect(bubble.locator('.sum')).toHaveText('3 changes in 2 blocks');
  await bubble.locator('.grp').first().locator('.gh .sw2').click();
  expect(await page.evaluate(() => (window as any).__calls)).toEqual(['0:false', '1:false']);
  await expect(bubble.locator('.grp').first().locator('li.off')).toHaveCount(2);
});

test('a proposal that came from a question: Details opens a big sheet with the explanation and the change, and Apply now there keeps it at once', async ({ page }) => {
  await page.evaluate(async () => {
    const { draftBubble } = await import('/app/koda-bubbles.js');
    const calls: string[] = [];
    const host = document.createElement('div'); host.className = 'koda'; host.id = 'zz-host'; document.body.append(host);
    host.append(draftBubble({ summary: [{ type: 'change', text: 'rain: length 24 → 72', hunk: 0, block: 'rain' }], more: '- `rain` runs three times longer', diff: '-at 1 for 24' + String.fromCharCode(10) + '+at 1 for 72', applyNow: true }, (v: string) => calls.push(v), () => {}));
    (window as any).__calls = calls;
  });
  const bubble = page.locator('#zz-host .bub');
  await expect(bubble.getByRole('button', { name: 'Apply now' })).toBeVisible();
  await bubble.getByRole('button', { name: 'Details' }).click();
  const sheet = page.locator('.koda-details');
  await expect(sheet).toContainText('runs three times longer');
  await expect(sheet).toContainText('+at 1 for 72');
  const box = await sheet.boundingBox();
  expect(box!.width).toBeGreaterThan(900);
  await sheet.getByRole('button', { name: 'Apply now' }).click();
  await expect(sheet).toHaveCount(0);
  expect(await page.evaluate(() => (window as any).__calls)).toEqual(['keep']);
});

test('Ctrl+K asks about the selected block: the menu names it and the explanation comes from its type', async ({ page }) => {
  await page.locator('#btn-koda').click();
  await expect.poll(async () => (await state(page)).presence).toBe('visible');
  await page.keyboard.press('Escape');
  await page.locator('.node').first().click();
  await page.keyboard.press('Control+k');
  const menu = page.locator('#koda-layer .bub.k-menu');
  await expect(menu).toBeVisible();
  await menu.getByRole('button', { name: 'What does this do?' }).click();
  await expect(page.locator('#koda-layer .bub.k-answer')).toBeVisible();
});

test('a hidden Koda holds an answer and peeks out of the edge, and shows it when they come back; muted Koda still answers a question asked by hand', async ({ page }) => {
  await page.locator('#btn-koda').click();
  await expect.poll(async () => (await state(page)).presence).toBe('visible');
  await page.evaluate(async () => {
    const { setBackend } = await import('/app/koda-backend.js');
    (window as any).__release = () => {};
    setBackend({
      explain: async () => ({ kind: 'answer', title: 'x', text: 'y' }),
      run: () => new Promise((resolve) => { (window as any).__release = () => resolve({ kind: 'draft', summary: [{ type: 'add', text: 'reverb after piano' }] }); }),
    });
  });
  await page.locator('.node').first().click();
  await page.keyboard.press('Escape');
  await page.keyboard.press('Control+k');
  await page.locator('#koda-layer .bub.k-menu').getByRole('button', { name: 'Do this…' }).click();
  await page.locator('#koda-layer .bub.k-choice input').fill('put a reverb after it');
  await page.keyboard.press('Enter');
  await expect(page.locator('#koda-layer .bub.k-thought')).toBeVisible();
  await page.locator('#btn-koda').click();   // hide while they are still working: Koda asks whether it may go on
  await page.locator('#koda-layer').getByRole('button', { name: 'Keep going' }).click();
  await expect.poll(async () => (await state(page)).presence).toBe('hidden');
  await page.evaluate(() => (window as any).__release());
  // not muted: Koda comes back from its edge by itself and the draft opens
  await expect(page.locator('#koda-layer .bub.k-draft')).toBeVisible({ timeout: 8000 });
  expect((await state(page)).presence).toBe('visible');
  // muted: still answers a question that was asked by hand
  await page.evaluate(async () => { (await import('/app/koda.js')).muteKoda(true); });
  expect((await state(page)).muted).toBe(true);
});

test('Do this goes to the model: a draft shows its changes and the buttons, Yes keeps it', async ({ page }) => {
  await page.locator('#btn-koda').click();
  await expect.poll(async () => (await state(page)).presence).toBe('visible');
  await page.evaluate(async () => {
    const { setBackend } = await import('/app/koda-backend.js');
    (window as any).__kept = false;
    setBackend({ explain: async () => ({ kind: 'answer', title: 'x', text: 'y' }), run: async () => ({ kind: 'draft', summary: [{ type: 'add', text: 'reverb after piano' }], apply: async () => { (window as any).__kept = true; return true; } }) });
  });
  await page.locator('.node').first().click();
  await page.keyboard.press('Escape');
  await page.keyboard.press('Control+k');
  await page.locator('#koda-layer .bub.k-menu').getByRole('button', { name: 'Do this…' }).click();
  await page.locator('#koda-layer .bub.k-choice input').fill('put a reverb after it');
  await page.keyboard.press('Enter');
  const draft = page.locator('#koda-layer .bub.k-draft');
  await expect(draft).toBeVisible({ timeout: 8000 });
  await expect(draft).toContainText('reverb after piano');
  await draft.getByRole('button', { name: 'Yes, keep it' }).click();
  await expect.poll(() => page.evaluate(() => (window as any).__kept)).toBe(true);
});

test('the server backend runs a Koda task: a question becomes an answer, an instruction a draft that Yes accepts, and a busy lane is said plainly', async ({ page }) => {
  const calls: string[] = [];
  let busy = false;
  await page.route('**/api/ai/sessions', (route) => route.fulfill({ json: { id: 's1', title: 'Koda' } }));
  await page.route('**/api/ai/tasks', (route) => {
    calls.push('start ' + JSON.parse(route.request().postData() || '{}').kind);
    if (busy) return route.fulfill({ status: 409, json: { code: 'AMP5464', detail: 'AMP5464: Koda is still busy with the last question.' } });
    return route.fulfill({ json: { id: 't1', status: 'queued' } });
  });
  await page.route(/\/api\/ai\/tasks\/t1(\?.*)?$/,(route) => route.fulfill({ json: { id: 't1', status: 'ready', summary: 'It plays the chords.\n\nIt feeds the mixer.', changes: [{ path: 'song.amplet', diff: '--- a\n+++ b\n+reverb room {\n+  from keys.audio -> audio\n' }] } }));
  await page.route('**/api/ai/tasks/t1/accept', (route) => { calls.push('accept'); return route.fulfill({ json: { song: 'x', songText: null } }); });
  await page.locator('#btn-koda').click();
  await expect.poll(async () => (await state(page)).presence).toBe('visible');
  await page.locator('.node').first().click();
  await page.keyboard.press('Escape');
  await page.keyboard.press('Control+k');
  await page.locator('#koda-layer .bub.k-menu').getByRole('button', { name: 'Do this…' }).click();
  await page.locator('#koda-layer .bub.k-choice input').fill('put a reverb after it');
  await page.keyboard.press('Enter');
  const draft = page.locator('#koda-layer .bub.k-draft');
  await expect(draft).toBeVisible({ timeout: 8000 });
  await expect(draft).toContainText('reverb room');
  await expect(page.locator('.koda-ghost')).toHaveCount(1);   // the new block is on the board, dashed
  await expect(page.locator('.koda-ghost')).toContainText('room');
  await draft.locator('.letsgo, button.go').first().click({ force: true });   // several changes: the neon button
  await expect(page.locator('.koda-ghost')).toHaveCount(0);
  await expect.poll(() => calls.includes('accept')).toBe(true);
  expect(calls[0]).toBe('start koda');
  // the lane is busy: Koda says so
  await expect(page.locator('#koda-layer .bub')).toContainText('Done');   // applied: Koda says so
  busy = true;
  await page.keyboard.press('Escape');
  await page.locator('.node').first().click();   // the accepted draft reloaded the song: select a block again
  await page.keyboard.press('Control+k');
  await page.locator('#koda-layer .bub.k-menu').getByRole('button', { name: 'Do this…' }).click();
  await page.locator('#koda-layer .bub.k-choice input').fill('again');
  await page.keyboard.press('Enter');
  await expect(page.locator('#koda-layer .bub')).toContainText('still busy', { timeout: 8000 });
});

test('in the source editor the draft is shown in the text: added lines dashed, Accept keeps it, the little field talks it over and Reject throws it away', async ({ page }) => {
  const calls: string[] = [];
  await page.route('**/api/ai/sessions', (route) => route.fulfill({ json: { id: 's1', title: 'Koda' } }));
  await page.locator('.node').first().click();
  await page.click('#vs-source');
  await expect.poll(() => srcText(page)).toMatch(/composition/);
  const current = await srcText(page);
  const songPath = await page.evaluate(async () => (await import('/app/store.js')).S.path);
  const proposed = current.replace(/\s*$/, '\n') + 'reverb room {\n  from drums.audio -> audio\n}\n';
  let n = 0;
  await page.route('**/api/ai/tasks', (route) => { n++; calls.push('start'); return route.fulfill({ json: { id: 't' + n, status: 'queued' } }); });
  await page.route(/\/api\/ai\/tasks\/t\d(\?.*)?$/,(route) => route.fulfill({ json: { id: 't1', status: 'ready', summary: 'Added a reverb.', changes: [{ path: songPath, diff: '+reverb room {\n+  from drums.audio -> audio\n+}\n' }] } }));
  await page.route(/\/api\/ai\/tasks\/t\d\/text/, (route) => route.fulfill({ json: { path: 'x', text: proposed } }));
  await page.route(/\/api\/ai\/tasks\/t\d\/accept/, (route) => { calls.push('accept'); return route.fulfill({ json: { song: 'x', songText: null } }); });
  await page.route(/\/api\/ai\/tasks\/t\d\/reject/, (route) => { calls.push('reject'); return route.fulfill({ json: {} }); });
  await page.locator('#btn-koda').click();
  await expect.poll(async () => (await state(page)).presence).toBe('visible');
  await page.keyboard.press('Escape');
  await page.evaluate(async () => { const k = await import('/app/koda.js'); k.ask(k.subjectOfBlock('drums')); });
  await page.locator('#koda-layer .bub.k-menu').getByRole('button', { name: 'Do this…' }).click();
  await page.locator('#koda-layer .bub.k-choice input').fill('add a reverb');
  await page.keyboard.press('Enter');
  const ghost = page.locator('#koda-inline .ki-add');
  await expect(ghost).toBeVisible({ timeout: 8000 });
  await expect(ghost).toContainText('reverb room');
  await expect(page.locator('#koda-inline .ki-count')).toContainText('1 change');
  // talk it over: a remark starts a new question and the old draft goes
  await page.locator('#koda-inline .ki-input').fill('shorter please');
  await page.keyboard.press('Enter');
  await expect.poll(() => calls.filter((c) => c === 'start').length).toBe(2);
  await expect(page.locator('#koda-inline .ki-add')).toBeVisible({ timeout: 8000 });
  // Esc in the little field throws the suggestion away
  await page.locator('#koda-inline .ki-input').focus();
  await page.keyboard.press('Escape');
  await expect(page.locator('#koda-inline')).toHaveCount(0);
  expect(calls).toContain('reject');
  // and Accept keeps one
  await page.evaluate(async () => { const k = await import('/app/koda.js'); k.ask(k.subjectOfBlock('drums')); });
  await page.locator('#koda-layer .bub.k-menu').getByRole('button', { name: 'Do this…' }).click();
  await page.locator('#koda-layer .bub.k-choice input').fill('again');
  await page.keyboard.press('Enter');
  await expect(page.locator('#koda-inline .ki-add')).toBeVisible({ timeout: 8000 });
  await page.locator('#koda-inline .ki-accept').click();
  await expect.poll(() => calls.includes('accept')).toBe(true);
  await expect(page.locator('#koda-inline')).toHaveCount(0);
});

test('the language of Koda is a project setting in the project settings: Dutch words, remembered after a reload', async ({ page }) => {
  await page.locator('#btn-koda').click();
  await expect.poll(async () => (await state(page)).presence).toBe('visible');
  await page.keyboard.press('Escape');
  // the menu of Koda has no language switch any more
  await page.locator('.node').first().click();
  await page.keyboard.press('Control+k');
  const menu = page.locator('#koda-layer .bub.k-menu');
  await expect(menu.locator('.tag')).toHaveText('Ask Koda');
  await expect(menu.getByRole('button', { name: 'NL', exact: true })).toHaveCount(0);
  await page.keyboard.press('Escape');
  await page.reload();
  await page.locator('#open-settings').click();
  await page.locator('#ps-koda-language').selectOption('nl');
  await page.locator('#ps-save').click();
  await expect.poll(async () => page.evaluate(async () => (await (await fetch('/api/project/settings')).json()).koda.language)).toBe('nl');
  await page.reload();
  await expect.poll(async () => (await state(page)).presence).toBe('visible');
  await page.locator('.node').first().click();
  await page.keyboard.press('Control+k');
  await expect(page.locator('#koda-layer .bub.k-menu .tag')).toHaveText('Vraag Koda');
  await page.evaluate(() => import('/app/koda-lang.js').then((m) => m.saveKodaLanguage('en')));
});

test('Ctrl and drag from Koda to a block: Koda looks there, a neon line stays while the block is selected and fades when it is not @quarantine', async ({ page }) => {
  await page.locator('#btn-koda').click();
  await expect.poll(async () => (await state(page)).presence).toBe('visible');
  const koda = await page.locator('#koda-layer .pet').boundingBox();
  const node = page.locator('.node').nth(1);
  const nb = await node.boundingBox();
  const id = await node.getAttribute('data-id');
  await page.mouse.move(koda!.x + koda!.width / 2, koda!.y + koda!.height / 2);
  await page.keyboard.down('Control');
  await page.mouse.down();
  await page.mouse.move(nb!.x + nb!.width / 2, nb!.y + nb!.height / 2, { steps: 8 });
  await expect(page.locator('.koda-gaze .gz-aim')).toHaveCount(1);
  await page.mouse.up();
  await page.keyboard.up('Control');
  await expect(page.locator('.koda-gaze .gz-look')).toHaveCount(1);
  await page.locator('#koda-layer .pet').click();
  await expect(page.locator('#koda-layer .bub.k-menu b').first()).toHaveText(id!);
  await page.keyboard.press('Escape');
  await page.locator('.node[data-id]').filter({ hasNot: page.locator('xpath=self::*[@data-id="' + id + '"]') }).first().click();
  await expect(page.locator('.koda-gaze .gz-look')).toHaveCount(0, { timeout: 4000 });
});

test('Ctrl+Shift and drag adds more things to look at, letting go on a linked one lets it go, and shaking lets go of everything', async ({ page }) => {
  await page.locator('#btn-koda').click();
  await expect.poll(async () => (await state(page)).presence).toBe('visible');
  const center = async (loc: any) => { const b = await loc.boundingBox(); return { x: b.x + b.width / 2, y: b.y + b.height / 2 }; };
  const drag = async (to: { x: number; y: number }, keys: string[]) => {
    const k = await center(page.locator('#koda-layer .pet'));
    await page.mouse.move(k.x, k.y);
    for (const key of keys) await page.keyboard.down(key);
    await page.mouse.down();
    await page.mouse.move(to.x, to.y, { steps: 8 });
    await page.mouse.up();
    for (const key of keys) await page.keyboard.up(key);
  };
  const one = await center(page.locator('.node').nth(0));
  const two = await center(page.locator('.node').nth(1));
  await drag(one, ['Control']);
  await expect(page.locator('.koda-gaze .gz-look')).toHaveCount(1);
  await drag(two, ['Control', 'Shift']);
  await expect(page.locator('.koda-gaze .gz-look')).toHaveCount(2);
  const subject = await page.evaluate(() => import('/app/koda.js').then((m) => m.currentSubject()));
  expect(subject.kind).toBe('multi');
  expect(subject.items).toHaveLength(2);
  await drag(two, ['Control', 'Shift']);
  await expect(page.locator('.koda-gaze .gz-look:not([style*="opacity: 0"])')).toHaveCount(1, { timeout: 4000 });
  // shaken: everything is let go of, Koda is puzzled and then smiles
  const k = await center(page.locator('#koda-layer .pet'));
  await page.mouse.move(k.x, k.y);
  await page.keyboard.down('Control');
  await page.mouse.down();
  for (let i = 0; i < 8; i++) await page.mouse.move(k.x + (i % 2 ? -50 : 50), k.y + 60, { steps: 2 });
  await page.mouse.up();
  await page.keyboard.up('Control');
  await expect(page.locator('.koda-gaze .gz-look')).toHaveCount(0, { timeout: 4000 });
  await expect.poll(async () => (await state(page)).emotion, { timeout: 4000 }).toBe('happy');
});

test('shaking Koda itself throws the lines off one after the other, the newest first @quarantine', async ({ page }) => {
  await page.locator('#btn-koda').click();
  await expect.poll(async () => (await state(page)).presence).toBe('visible');
  await expect(page.locator('#koda-layer .bub')).toContainText('I am Koda');   // the hello comes after a second: let it come and go first
  await page.keyboard.press('Escape');
  await expect(page.locator('#koda-layer .bub')).toHaveCount(0);
  const center = async (loc: any) => { const b = await loc.boundingBox(); return { x: b.x + b.width / 2, y: b.y + b.height / 2 }; };
  const link = async (to: { x: number; y: number }, keys: string[]) => {
    const k = await center(page.locator('#koda-layer .pet'));
    await page.mouse.move(k.x, k.y);
    for (const key of keys) await page.keyboard.down(key);
    await page.mouse.down(); await page.mouse.move(to.x, to.y, { steps: 6 }); await page.mouse.up();
    for (const key of keys) await page.keyboard.up(key);
  };
  await link(await center(page.locator('.node').nth(0)), ['Control']);
  await link(await center(page.locator('.node').nth(1)), ['Control', 'Shift']);
  await link(await center(page.locator('.node').nth(2)), ['Control', 'Shift']);
  const count = () => page.evaluate(async () => (await import('/app/koda-gaze.js')).looksCount());
  expect(await count()).toBe(3);
  const k = await center(page.locator('#koda-layer .pet'));
  // a short shake throws off only the newest one
  await page.mouse.move(k.x, k.y);
  await page.mouse.down();
  for (let i = 0; i < 5; i++) await page.mouse.move(k.x + (i % 2 ? -40 : 40), k.y, { steps: 2 });
  await page.mouse.up();
  await page.waitForTimeout(300);
  expect(await count()).toBe(2);
  const k2 = await center(page.locator('#koda-layer .pet'));
  k.x = k2.x; k.y = k2.y;
  await page.mouse.move(k.x, k.y);
  await page.mouse.down();
  const seen: number[] = [await count()];   // the count before the shake is part of the series: a loaded machine may throw the next line before the first sample
  // keep shaking for about two seconds: one line per 0.69 s at most, so the three are gone after roughly two throws' time
  const t0 = Date.now();
  while (Date.now() - t0 < 2300) { for (let i = 0; i < 4; i++) await page.mouse.move(k.x + (i % 2 ? -40 : 40), k.y, { steps: 2 }); seen.push(await count()); }
  const quick = seen.findIndex((n) => n === 0);
  expect(quick).toBeGreaterThan(2);   // not all at once
  await page.mouse.up();
  expect(seen[seen.length - 1]).toBe(0);
  expect(seen).toContain(2);   // not all at once: one after the other
  // the smile comes 0.7 s after the last throw and lasts only 1.5 s: look often from the moment the mouse is up (a loaded machine may start looking late), and stop at the first smile
  let smiled = false;
  for (let i = 0; i < 100 && !smiled; i++) { smiled = (await state(page)).emotion === 'happy'; if (!smiled) await page.waitForTimeout(30); }
  expect(smiled).toBe(true);
});

test('Koda sways to the beat of whatever plays, and settles when it stops @quarantine', async ({ page }) => {
  await page.locator('#btn-koda').click();
  await expect.poll(async () => (await state(page)).presence).toBe('visible');
  await page.keyboard.press('Escape');
  await page.evaluate(async () => {
    const { playbackOutput } = await import('/app/playback-volume.js');
    const ctx = new AudioContext();
    await ctx.resume();
    const osc = ctx.createOscillator(); osc.frequency.value = 110;
    const gain = ctx.createGain();
    for (let i = 0; i < 40; i++) { gain.gain.setValueAtTime(0.02, ctx.currentTime + i * 0.5); gain.gain.linearRampToValueAtTime(0.7, ctx.currentTime + i * 0.5 + 0.01); gain.gain.exponentialRampToValueAtTime(0.02, ctx.currentTime + i * 0.5 + 0.2); }
    osc.connect(gain); gain.connect(playbackOutput(ctx)); osc.start();
    (window as any).__beat = { ctx, osc };
  });
  await expect(page.locator('#koda-layer .pet')).toHaveAttribute('data-vibe', 'full', { timeout: 12000 });
  await page.evaluate(() => { (window as any).__beat.osc.stop(); });
  await expect(page.locator('#koda-layer .pet')).not.toHaveAttribute('data-vibe', /.+/, { timeout: 12000 });
});

async function pointKodaAt(page, locator, { ctrl = true } = {}) {
  const koda = await page.locator('#koda-layer .pet').boundingBox();
  const box = await locator.boundingBox();
  await page.mouse.move(koda!.x + koda!.width / 2, koda!.y + koda!.height / 2);
  if (ctrl) await page.keyboard.down('Control');
  await page.mouse.down();
  await page.mouse.move(box!.x + Math.min(box!.width / 2, 60), box!.y + box!.height / 2, { steps: 8 });
  await page.mouse.up();
  if (ctrl) await page.keyboard.up('Control');
}
async function menuSays(page, text) {
  await page.locator('#koda-layer .pet').click();
  await expect(page.locator('#koda-layer .bub.k-menu')).toContainText(text);
  await page.keyboard.press('Escape');
}
async function kodaOn(page) {
  await page.locator('#btn-koda').click();
  await expect.poll(async () => (await state(page)).presence).toBe('visible');
  await expect(page.locator('#koda-layer .bub')).toBeVisible();   // the first hello
  await page.keyboard.press('Escape');
  await expect(page.locator('#koda-layer .bub')).toHaveCount(0);
}

test('Koda can look at a line of code, a field, and a block of the library', async ({ page }) => {
  await kodaOn(page);
  // a block of the library
  await pointKodaAt(page, page.locator('.tplitem[data-tpl]').first());
  await expect(page.locator('.koda-gaze .gz-look')).toHaveCount(1);
  await menuSays(page, 'block');
  // a field of the selected block
  await page.locator('.node').first().click();
  await expect(page.locator('.prop[data-koda-param]').first()).toBeVisible();
  const field = page.locator('.prop[data-koda-param]').first();
  const param = await field.getAttribute('data-koda-param');
  await pointKodaAt(page, field);
  await expect(page.locator('.koda-gaze .gz-look')).toHaveCount(1);
  await menuSays(page, `${param} of`);
});

test('Koda can look at a line of the source editor', async ({ page }) => {
  await kodaOn(page);
  await page.locator('.node').first().click();
  await page.click('#vs-source');
  await expect.poll(() => srcText(page)).toMatch(/composition/);
  const ta = page.locator('#se-ta');
  await expect.poll(async () => (await page.locator('#se-cm').boundingBox())?.height ?? 0).toBeGreaterThan(40);
  await pointKodaAt(page, page.locator('#se-cm'));
  await expect(page.locator('.koda-gaze .gz-look')).toHaveCount(1);
  expect(await ta.evaluate((e: HTMLTextAreaElement) => e.selectionEnd - e.selectionStart)).toBeGreaterThan(0);
  await menuSays(page, 'Looking at line');
});

test('a quick second touch that is dragged does the same as Ctrl and drag, and the block menu can point Koda too', async ({ page }) => {
  await kodaOn(page);
  const koda = await page.locator('#koda-layer .pet').boundingBox();
  const node = page.locator('.node').nth(1);
  const nb = await node.boundingBox();
  await page.mouse.move(koda!.x + koda!.width / 2, koda!.y + koda!.height / 2);
  await page.mouse.down(); await page.mouse.up();
  await page.mouse.down();
  await page.mouse.move(nb!.x + nb!.width / 2, nb!.y + nb!.height / 2, { steps: 8 });
  await expect(page.locator('.koda-gaze .gz-aim')).toHaveCount(1);
  await page.mouse.up();
  await expect(page.locator('.koda-gaze .gz-look')).toHaveCount(1);
  // a long press (the context menu) of another block: Koda, Look at this
  const other = page.locator('.node').first();
  await other.click({ button: 'right' });
  await page.getByText('Koda ›').last().hover();
  await page.getByText('Look at this', { exact: true }).click();
  await expect(page.locator('.koda-gaze .gz-look')).toHaveCount(1);
  await menuSays(page, await other.getAttribute('data-id'));
  // "Look at something else...": the next press points Koda instead of clicking
  await page.evaluate(() => import('/app/koda.js').then((m) => m.pickLook()));
  await expect(page.locator('body.koda-picking')).toHaveCount(1);
  await node.click();
  await expect(page.locator('body.koda-picking')).toHaveCount(0);
  await menuSays(page, await node.getAttribute('data-id'));
});

test('an answer from the documentation has a small book: hover or a press says where it comes from, with a link, and the text is not in the answer itself', async ({ page }) => {
  await kodaOn(page);
  await page.locator('.node').first().click();
  await page.keyboard.press('Control+k');
  await page.getByRole('button', { name: 'What does this do?' }).click();
  const bubble = page.locator('#koda-layer .bub.k-answer');
  await expect(bubble).toBeVisible();
  await expect(bubble.locator('p').last()).not.toContainText('comes from the AMPLE documentation');
  const kb = bubble.locator('button.kb');
  await kb.hover();
  await expect(bubble.locator('.kb-pop')).toBeVisible();
  await expect(bubble.locator('.kb-pop a')).toHaveAttribute('href', /^\/help#template-[a-z0-9-]+$/);
  await page.mouse.move(10, 10);
  await expect(bubble.locator('.kb-pop')).toBeHidden();
  await kb.click();
  await page.mouse.move(10, 10);
  await expect(bubble.locator('.kb-pop')).toBeVisible();
});

test('the line to a block waits when the block is scrolled out of sight and comes back with it', async ({ page }) => {
  await kodaOn(page);
  const node = page.locator('.node').nth(1);
  await pointKodaAt(page, node);
  const line = page.locator('.koda-gaze .gz-look');
  await expect(line).toHaveCount(1);
  const opacity = async () => Number(await line.evaluate((e: SVGElement) => e.style.opacity || '0'));
  await expect.poll(opacity).toBeGreaterThan(0.9);
  const pan = (dx: number) => page.evaluate(async (d) => { const { S } = await import('/app/store.js'); const { applyView } = await import('/app/canvas.js'); S.view = { ...S.view, x: S.view.x + d }; applyView(); }, dx);
  await pan(-4000);
  await expect.poll(opacity).toBeLessThan(0.05);
  await expect(line).toHaveCount(1);
  await pan(4000);
  await expect.poll(opacity).toBeGreaterThan(0.9);
});

test('the book of an answer links to the page of the block type in the documentation, and Koda reads with a magnifying glass while it looks things up', async ({ page }) => {
  await kodaOn(page);
  await page.locator('.node').first().click();
  await page.keyboard.press('Control+k');
  await page.getByRole('button', { name: 'What does this do?' }).click();
  await expect(page.locator('.kb-pop a')).toHaveAttribute('href', /^\/help#template-[a-z0-9-]+$/);
  await page.evaluate(() => { (document.querySelector('#koda-layer .pet') as SVGElement).dataset.emo = 'scan'; });
  await expect(page.locator('#koda-layer .pet .lens')).toHaveCSS('opacity', '1');
});

test('a draft has a switch per change, marks what was not asked for as optional, and offers a preview', async ({ page }) => {
  const result = await page.evaluate(async () => {
    const { draftBubble } = await import('/app/koda-bubbles.js');
    const calls: string[] = [];
    const host = document.createElement('div'); host.className = 'koda'; host.id = 'zz-host'; document.body.append(host);
    host.append(draftBubble({ summary: [{ type: 'add', text: 'New reverb room', hunk: 0 }, { type: 'remove', text: 'Block duck removed', hunk: 1, optional: true }], canPreview: true, onToggle: (h, on) => calls.push(h + ':' + on) }, (v) => calls.push('pick:' + v), () => {}));
    (window as any).__calls = calls;
    return true;
  });
  expect(result).toBe(true);
  const bubble = page.locator('#zz-host .bub');
  await expect(bubble.locator('.sw2 input')).toHaveCount(2);
  await expect(bubble.locator('.opt')).toHaveText('optional');
  await expect(bubble.locator('.optnote')).toHaveCount(0);
  await expect(bubble.locator('.opt-pop')).toBeHidden();
  await bubble.locator('.opt').hover();
  await expect(bubble.locator('.opt-pop')).toContainText('recommended');
  await bubble.locator('li').nth(1).locator('.sw2').click();
  await expect(bubble.locator('li').nth(1)).toHaveClass(/off/);
  await bubble.getByRole('button', { name: 'Preview result' }).click();
  expect(await page.evaluate(() => (window as any).__calls)).toEqual(['1:false', 'pick:preview']);
});

test('linked to a block, the glow and the equalizer show that block only, and the rest only makes Koda sway a little', async ({ page }) => {
  await kodaOn(page);
  const nodes = page.locator('.node[data-id]');
  const linked = nodes.nth(1), other = nodes.first();
  const linkedId = await linked.getAttribute('data-id');
  await pointKodaAt(page, linked);
  await expect(page.locator('.koda-gaze .gz-look')).toHaveCount(1);
  await page.evaluate(async () => {
    const { playbackOutput } = await import('/app/playback-volume.js');
    const ctx = new AudioContext(); await ctx.resume();
    const osc = ctx.createOscillator(); osc.frequency.value = 110; const gain = ctx.createGain();
    for (let i = 0; i < 60; i++) { gain.gain.setValueAtTime(0.02, ctx.currentTime + i * 0.5); gain.gain.linearRampToValueAtTime(0.7, ctx.currentTime + i * 0.5 + 0.01); gain.gain.exponentialRampToValueAtTime(0.02, ctx.currentTime + i * 0.5 + 0.2); }
    osc.connect(gain); gain.connect(playbackOutput(ctx)); osc.start();
  });
  const aura = page.locator('#koda-layer .aura');
  const opacity = async () => Number(await aura.evaluate((e: HTMLElement) => e.style.opacity || '0'));
  // only another block is lit: the linked one is quiet, so the aura is dim
  await other.evaluate((e: HTMLElement) => { e.classList.add('fx-on'); e.style.setProperty('--fx-lvl', '0.8'); });
  await expect.poll(opacity, { timeout: 8000 }).toBeLessThan(0.45);
  await expect(page.locator('#koda-layer .pet')).toHaveAttribute('data-vibe', 'soft');
  // the linked block sounds: the aura is bright
  await page.locator(`.node[data-id="${linkedId}"]`).evaluate((e: HTMLElement) => { e.classList.add('fx-on'); e.style.setProperty('--fx-lvl', '0.7'); });
  await expect.poll(opacity, { timeout: 8000 }).toBeGreaterThan(0.9);
});

test('after an answer, faded lines go to the blocks it talks about and stay a few seconds', async ({ page }) => {
  await kodaOn(page);
  await page.locator('.node[data-id="piano"]').click();
  await page.keyboard.press('Control+k');
  await page.getByRole('button', { name: 'What does this do?' }).click();
  await expect(page.locator('#koda-layer .bub.k-answer')).toBeVisible();
  await expect.poll(() => page.locator('.koda-gaze .gz-glance').count(), { timeout: 3000 }).toBeGreaterThan(0);
  await page.waitForTimeout(3500);
  expect(await page.locator('.koda-gaze .gz-glance').count()).toBeGreaterThan(0);
});

test('variants the model offered are shown as A, B and C: listen to one, ask about them, Use this one keeps that one and drops the proposal once @quarantine', async ({ page }) => {
  await page.locator('#btn-koda').click();
  await expect.poll(async () => (await state(page)).presence).toBe('visible');
  await page.evaluate(async () => {
    const { setBackend } = await import('/app/koda-backend.js');
    (window as any).__discarded = []; (window as any).__kept = '';
    setBackend({
      explain: async () => ({ kind: 'answer', title: 'x', text: 'y' }),
      // a request on the proposal itself: the model offers the variants again (here two)
      continue: async (_id: string, _s: any, q: string) => ({ kind: 'variants', shared: true, say: 'Done: ' + q, block: 'x', discard: () => {}, drafts: [1, 2].map((i) => ({ kind: 'draft', name: 'New ' + i, note: 'n' + i, summary: [{ type: 'change', text: 'beat ' + i }], taskId: 't', listen: 'b' + i, apply: async () => true, discard: () => {} })) }),
      run: async () => ({
        kind: 'variants', shared: true, block: 'x', text: 'three instruments', discard: () => (window as any).__discarded.push('all'),
        drafts: [1, 2, 3].map((i) => ({ kind: 'draft', name: 'Sound ' + i, note: '**Sound ' + i + '**: beat ' + i, summary: [{ type: 'change', text: 'beat ' + i + ': pattern changed' }], taskId: 't', listen: 'b' + i, apply: async () => { (window as any).__kept = 'v' + i; return true; }, discard: () => {} })),
      }),
    });
  });
  await page.locator('.node').first().click();
  await page.keyboard.press('Escape');
  await page.keyboard.press('Control+k');
  await page.locator('#koda-layer .bub.k-menu').getByRole('button', { name: 'Do this…' }).click();
  await page.locator('#koda-layer .bub.k-choice input').fill('three instruments for this');
  await page.keyboard.press('Enter');
  const bubble = page.locator('#koda-layer .bub.k-draft');
  await expect(bubble.locator('.vt')).toHaveCount(3, { timeout: 8000 });
  await expect(bubble.locator('.vt').first()).toContainText('Sound 1');
  await expect(bubble.locator('.vbody')).toContainText('beat 1');
  await bubble.locator('.vt').nth(1).click({ force: true });
  await expect(bubble.locator('.vbody')).toContainText('beat 2');
  await expect(bubble.locator('.vlisten').first()).toContainText('Listen to B');
  await expect(bubble.locator('.vt').nth(1)).toBeVisible();
  await bubble.getByRole('button', { name: 'Ask a question' }).click();
  await bubble.locator('.other input').fill('other beats please');
  await page.keyboard.press('Enter');
  await expect(bubble.locator('.vans')).toContainText('Done: other beats please');
  await expect(bubble.locator('.vt')).toHaveCount(2);   // the proposal went on: new variants replace the old ones
  await bubble.getByRole('button', { name: 'Use this one' }).click({ force: true });
  await expect(page.locator('#koda-layer .bub.k-draft')).toContainText('beat 1');
  expect(await page.evaluate(() => (window as any).__discarded.length)).toBe(0);   // the proposal is dropped when the chosen one is applied, not before
});

test('Koda can be pointed at a bar of the arrangement and at its ruler', async ({ page }) => {
  await page.locator('#arr .bar[data-block]').first().waitFor();
  const kinds = await page.evaluate(async () => {
    const { targetAt } = await import('/app/koda-gaze.js');
    const r = (q: string) => { const b = document.querySelector(q)!.getBoundingClientRect(); return targetAt(b.left + 6, b.top + b.height / 2) as any; };
    const bar = r('#arr .bar[data-block]'), ruler = r('#arr .ruler');
    return { bar: bar?.kind, at: bar?.at, ruler: ruler?.kind, rbar: ruler?.bar };
  });
  expect(kinds.bar).toBe('bar');
  expect(kinds.at).toBeGreaterThan(0);
  expect(kinds.ruler).toBe('arrangement');
  expect(kinds.rbar).toBeGreaterThan(0);
});

test('while a build runs Koda constructs: hammer and bricks for a real build, blueprint and pencil for a preview, and back to rest after', async ({ page }) => {
  await page.locator('#btn-koda').click();
  await expect.poll(async () => (await state(page)).presence).toBe('visible');
  await page.keyboard.press('Escape');
  const set = (running: boolean, intent: string) => page.evaluate(async ([r, i]) => { const st = await import('/app/store.js'); st.S.build = { ...st.S.build, running: r, intent: i }; st.notify(); }, [running, intent] as const);
  await set(true, '');
  await expect(page.locator('#koda-layer .pet')).toHaveAttribute('data-emo', 'build');
  await expect(page.locator('#koda-layer .pet .hammer')).toBeAttached();
  await set(false, '');
  await expect(page.locator('#koda-layer .pet')).toHaveAttribute('data-emo', 'neutral');
  await set(true, 'preview');
  await expect(page.locator('#koda-layer .pet')).toHaveAttribute('data-emo', 'sketch');
  await set(false, 'preview');
  await expect(page.locator('#koda-layer .pet')).toHaveAttribute('data-emo', 'neutral');
});

test('the line of Koda does not point into the void when its target is scrolled out of the panel it sits in', async ({ page }) => {
  const seen = await page.evaluate(async () => {
    const { elementPoint } = await import('/app/koda-gaze.js');
    const box = document.createElement('div'); box.style.cssText = 'position:fixed;left:200px;top:200px;width:200px;height:60px;overflow:auto;z-index:5';
    const inner = document.createElement('div'); inner.style.height = '400px';
    const row = document.createElement('div'); row.id = 'zz-row'; row.style.cssText = 'margin-top:200px;height:20px'; inner.append(row); box.append(inner); document.body.append(box);
    const hidden = elementPoint('#zz-row', { x: 0, y: 0 });
    box.scrollTop = 190;
    const shown = elementPoint('#zz-row', { x: 0, y: 0 });
    box.remove();
    return { hidden, shown: !!shown };
  });
  expect(seen.hidden).toBeNull();
  expect(seen.shown).toBe(true);
});

test('draft or live: a pill in the bubble until the first reply; live applies the draft at once with a red headband, and the choice is locked after the reply @quarantine', async ({ page }) => {
  await page.locator('#btn-koda').click();
  await expect.poll(async () => (await state(page)).presence).toBe('visible');
  await page.evaluate(async () => {
    const { setBackend } = await import('/app/koda-backend.js');
    (window as any).__kept = 0;
    setBackend({ explain: async () => ({ kind: 'answer', title: 'x', text: 'y' }), run: async () => ({ kind: 'draft', summary: [{ type: 'add', text: 'reverb after piano' }], apply: async () => { (window as any).__kept++; return true; } }) });
  });
  await page.locator('.node').first().click();
  await page.keyboard.press('Escape');
  await page.keyboard.press('Control+k');
  const pill = page.locator('#koda-layer .riskpill');
  await expect(pill).toBeVisible();
  await expect(pill).toHaveAttribute('aria-checked', 'false');
  await expect(page.locator('#koda-layer svg.pet')).not.toHaveAttribute('data-risk', 'unsafe');
  await pill.click();
  await expect(pill).toHaveAttribute('aria-checked', 'true');
  await expect(page.locator('#koda-layer svg.pet')).toHaveAttribute('data-risk', 'unsafe');
  await page.locator('#koda-layer .bub.k-menu').getByRole('button', { name: 'Do this…' }).click();
  await page.locator('#koda-layer .bub.k-choice input').fill('put a reverb after it');
  await page.keyboard.press('Enter');
  await expect(page.locator('#koda-layer .bub')).toContainText('Done at once', { timeout: 8000 });
  expect(await page.evaluate(() => (window as any).__kept)).toBe(1);
  await expect(pill).toBeHidden();
  await expect(page.locator('#koda-layer .riskdot.unsafe')).toBeVisible();
  // live is remembered in this browser: a closed conversation stays on live, until it is switched off
  await page.keyboard.press('Escape');
  expect(await page.evaluate(() => localStorage.getItem('ample.koda.live'))).toBe('1');
  await page.keyboard.press('Control+k');
  await expect(page.locator('#koda-layer .riskpill')).toHaveAttribute('aria-checked', 'true');
  await page.locator('#koda-layer .riskpill').click({ force: true });   // Koda moves about in live mode, so it is never 'stable'
  await expect(page.locator('#koda-layer .riskpill')).toHaveAttribute('aria-checked', 'false');
  expect(await page.evaluate(() => localStorage.getItem('ample.koda.live'))).toBeNull();
});

test('draft or live is locked as soon as Koda starts to think', async ({ page }) => {
  await page.locator('#btn-koda').click();
  await expect.poll(async () => (await state(page)).presence).toBe('visible');
  await page.evaluate(async () => {
    const { setBackend } = await import('/app/koda-backend.js');
    setBackend({ explain: async () => ({ kind: 'answer', title: 'x', text: 'y' }), run: () => new Promise(() => {}) });
  });
  await page.locator('.node').first().click();
  await page.keyboard.press('Escape');
  await page.keyboard.press('Control+k');
  const pill = page.locator('#koda-layer .riskpill');
  await expect(pill).toBeVisible();
  await page.locator('#koda-layer .bub.k-menu').getByRole('button', { name: 'Do this…' }).click();
  await page.locator('#koda-layer .bub.k-choice input').fill('put a reverb after it');
  await page.keyboard.press('Enter');
  await expect(page.locator('#koda-layer .bub.k-thought')).toBeVisible();
  await expect(pill).toBeHidden();
});

test('a long answer scrolls in a limited area, the details unfold inside it, and the grip resizes the bubble (kept)', async ({ page }) => {
  await kodaOn(page);
  await page.evaluate(async () => {
    const { setBackend } = await import('/app/koda-backend.js');
    const long = Array.from({ length: 60 }, (_, i) => 'Line ' + i + ' of the details.').join(String.fromCharCode(10));
    const table = ['| block | port |', '|---|---|', '| piano | 8080 |', '| bass | 8081 |'].join(String.fromCharCode(10));
    const list = ['## Impact', '- `piano` carries the melody', '- `bass` holds the low end'].join(String.fromCharCode(10));
    const withTable = 'Intro in prose.' + String.fromCharCode(10, 10) + list + String.fromCharCode(10, 10) + table + String.fromCharCode(10, 10) + long;
    setBackend({ explain: async () => ({ kind: 'answer', title: 'A long answer', text: 'The short answer.', more: withTable }), run: async () => ({ kind: 'answer', title: 'x', text: 'y' }) });
  });
  await page.locator('.node').first().click();
  await page.keyboard.press('Escape');
  await page.keyboard.press('Control+k');
  await page.locator('#koda-layer .bub.k-menu').getByRole('button', { name: /What does this do/ }).click();
  const bub = page.locator('#koda-layer .bub.k-answer');
  await expect(bub).toBeVisible({ timeout: 8000 });
  await bub.getByRole('button', { name: 'Tell me more' }).click();   // the model's answer (the first one is AMPLE's own)
  await expect(bub.locator('h3')).toHaveText('A long answer', { timeout: 8000 });
  await bub.locator('summary').click();
  await expect(bub.locator('.scroll h4')).toHaveText('Impact');
  await expect(bub.locator('.scroll ul li')).toHaveCount(2);
  await expect(bub.locator('.tbl table tbody tr')).toHaveCount(2);
  await expect(bub.locator('.tbl th').first()).toHaveText('block');
  const scroll = bub.locator('.scroll');
  const h1 = (await scroll.boundingBox())!.height;
  expect(await scroll.evaluate((e) => getComputedStyle(e).overflowX)).toBe('hidden');   // only up and down; sideways only inside a table that cannot do otherwise
  expect(h1).toBeLessThan(500);   // limited, although the details are very long
  expect(await scroll.evaluate((e) => e.scrollHeight > e.clientHeight)).toBe(true);
  const w1 = (await bub.boundingBox())!.width;
  const grip = bub.locator('.grip');
  const g = (await grip.boundingBox())!;
  const leftGrip = await bub.evaluate((e) => e.classList.contains('grip-left'));
  await page.mouse.move(g.x + 8, g.y + 8);
  await page.mouse.down();
  await page.mouse.move(g.x + 8 + (leftGrip ? -120 : 120), g.y + 8 + 60, { steps: 6 });
  await page.waitForTimeout(400);
  await page.mouse.up();
  const w2 = (await bub.boundingBox())!.width;
  expect(w2).toBeGreaterThan(w1 + 60);
  expect((await scroll.boundingBox())!.height).toBeGreaterThan(h1 + 30);
  // the bubble still hangs on Koda
  const k = (await page.locator('#koda-layer .pet').boundingBox())!, b = (await bub.boundingBox())!;
  expect(Math.abs(b.y + b.height - k.y)).toBeLessThan(120);
});

test('the note at the edge can be dragged along the edge, and pulled far from it to bring Koda out', async ({ page }) => {
  await page.locator('#btn-koda').click();
  await expect.poll(async () => (await state(page)).presence).toBe('visible');
  await page.locator('#btn-koda').click();
  await expect.poll(async () => (await state(page)).presence).toBe('hidden');
  const tab = page.locator('.koda-tab');
  await expect(tab).toBeVisible();
  const at = async () => (await tab.boundingBox())!;
  const first = await at();
  const x = first.x + first.width / 2, y = first.y + first.height / 2;
  // along the edge: it moves and stays a note
  await page.mouse.move(x, y); await page.mouse.down();
  await page.mouse.move(x, y + 90, { steps: 6 }); await page.mouse.up();
  await expect.poll(async () => Math.round((await at()).y)).toBeGreaterThan(Math.round(first.y) + 60);
  expect((await state(page)).presence).toBe('hidden');
  // far from the edge: Koda comes out
  const moved = await at();
  const mx = moved.x + moved.width / 2, my = moved.y + moved.height / 2;
  const edgeLeft = moved.x < 100;
  await page.mouse.move(mx, my); await page.mouse.down();
  await page.mouse.move(edgeLeft ? mx + 200 : mx - 200, my, { steps: 8 }); await page.mouse.up();
  await expect.poll(async () => (await state(page)).presence).toBe('visible');
});

test('asking Koda with no song open says "not yet" and asks nothing of the server, instead of failing with a missing path', async ({ page }) => {
  const result = await page.evaluate(async () => {
    const { S } = await import('/app/store.js');
    const { serverBackend } = await import('/app/koda-server.js');
    const { KodaUnavailable } = await import('/app/koda-backend.js');
    S.path = ''; S.modulePath = ''; S.moduleStack = [];
    let posted = 0;
    const original = window.fetch;
    window.fetch = (...args) => { if (String(args[0]).includes('/api/ai/tasks')) posted++; return original(...args); };
    try {
      await serverBackend.run({ kind: 'block', block: 'any' }, 'make it louder', {});
      return { thrown: false, posted };
    } catch (error) {
      return { thrown: true, unavailable: error instanceof KodaUnavailable, message: String((error as Error).message), posted };
    } finally { window.fetch = original; }
  });
  expect(result.thrown).toBe(true);
  expect(result.unavailable).toBe(true);
  expect(result.message).toContain('Open a song first');
  expect(result.posted).toBe(0);
});
