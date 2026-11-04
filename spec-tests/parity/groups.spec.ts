import { test, expect } from '@playwright/test';
import { api, freshServer, node, openApp, state, srcText } from '../../web/tests/support';

freshServer({ files: { 'modules/generated-midi.module': `module generated-midi version 1.0
output audio mix.mix

midi-clip clip at 1 for 1 {
  notes 1:0:1:60:80
}
gm instrument at 1 for 1 {
  from clip.notes -> notes
  program 0
}
mixer mix {
  from instrument.audio -> audio1
  gains 0 0 0 0
}
`, 'modules/advanced-midi.module': `module advanced-midi version 1.0
output audio mix.mix

# midi-source version=1 format=1 ppq=480
# midi-fact feature=ties
# source-event track=0 tick=0 kind=Meta channel=0 data1=0 data2=0 meta=51 payload=07A120
midi-clip clip at 1 for 1 {
  notes 1:0:1:60:80
}
gm instrument at 1 for 1 {
  from clip.notes -> notes
  program 0
}
mixer mix {
  from instrument.audio -> audio1
  gains 0 0 0 0
}
` } });

test.beforeEach(async ({ page }) => { await openApp(page); });

async function groupTwo(page) {
  await node(page, 'bass').locator('.nh .t').click();
  await node(page, 'piano').locator('.nh .t').click({ modifiers: ['Control'] });
  await page.keyboard.press('Control+g');
}

test('grouping two blocks hides them behind a group with ports and leaves the source unchanged', async ({ page }) => {
  const before = (await state(page)).text;
  await groupTwo(page);
  await expect(page.locator('.node.grp')).toHaveCount(1);
  await expect(node(page, 'bass')).toHaveCount(0);
  expect((await state(page)).text).toBe(before);
});

test('double-click opens the group, the breadcrumb goes back', async ({ page }) => {
  await groupTwo(page);
  await page.locator('.node.grp').dispatchEvent('dblclick');
  await expect(node(page, 'bass')).toBeVisible();
  await expect(node(page, 'in')).toBeVisible();
  expect((await state(page)).stack.length).toBe(2);
  await page.locator('#crumbs span.c').first().click();
  await expect(node(page, 'bass')).toHaveCount(0);
});

test('ungroup puts the blocks back and the source round trip is identical', async ({ page }) => {
  const before = (await state(page)).text;
  await groupTwo(page);
  await page.keyboard.press('Control+Shift+g');
  await expect(page.locator('.node.grp')).toHaveCount(0);
  await expect(node(page, 'bass')).toBeVisible();
  expect((await state(page)).text).toBe(before);
});

test('the group arrow hides and shows the contents preview', async ({ page }) => {
  await groupTwo(page);
  await expect(page.locator('.node.grp svg')).toHaveCount(1);
  await page.locator('.node.grp .nh span[title="Show or hide the contents"]').click();
  await expect(page.locator('.node.grp svg')).toHaveCount(0);
});

test('groups survive a reload', async ({ page }) => {
  await groupTwo(page);
  await page.reload();
  await expect(page.locator('.node.grp')).toHaveCount(1);
});

test('module enter navigation survives reload and returns through the song breadcrumb', async ({ page }) => {
  const doc = await api('/api/doc?path=song.amplet');
  const text = `${doc.text}\nuse generated-midi@1.0 as imported {\n}\n`;
  await api('/api/doc', { method: 'PUT', body: { path: 'song.amplet', text, baseVersion: doc.version } });
  await api('/api/doc/save', { method: 'POST', body: { path: 'song.amplet' } });
  await page.reload();
  await expect(node(page, 'imported')).toBeVisible();
  await node(page, 'imported').dblclick();
  await expect(node(page, 'clip')).toBeVisible();
  await expect(node(page, 'instrument')).toBeVisible();
  await expect.poll(async () => (await api('/api/file?path=notes/song.amplet.module-navigation.json')).text).toBe('{"stack":[{"instance":"imported","path":"modules/generated-midi.module"}],"path":"modules/generated-midi.module"}');
  await page.reload();
  await expect.poll(() => page.evaluate(() => (window as any).AMPLE_STATE.moduleStack.length)).toBe(1);
  const moduleDoc = await api('/api/module/doc?path=modules/generated-midi.module');
  expect(moduleDoc.diagnostics.filter((diagnostic: { severity: string }) => diagnostic.severity === 'error')).toEqual([]);
  await expect(node(page, 'clip')).toBeVisible();
  await page.locator('#crumbs span.c').first().evaluate(crumb => (crumb as HTMLElement).click());
  await expect(node(page, 'imported')).toBeVisible();
});

async function enterAdvancedModule(page) {
  const doc = await api('/api/doc?path=song.amplet');
  // the server is shared by the tests of this file: the song may have the block from an earlier test already
  if (!doc.text.includes('use advanced-midi@1.0 as advanced')) {
    const text = `${doc.text}\nuse advanced-midi@1.0 as advanced {\n}\n`;
    await api('/api/doc', { method: 'PUT', body: { path: 'song.amplet', text, baseVersion: doc.version } });
    await api('/api/doc/save', { method: 'POST', body: { path: 'song.amplet' } });
  }
  await page.reload();
  await page.waitForSelector('body[data-ready="1"]');
  // the app reopens where the song was left (M24 keeps the navigation with the song): inside the module when an earlier test left it open
  // go to the song first (an earlier test may have left a module open), then enter the module
  await expect(page.locator('#crumbs .c').first()).toBeVisible();
  if (await page.locator('#crumbs .c').count() > 1) await page.locator('#crumbs .c').first().click();
  await expect(node(page, 'advanced')).toBeVisible();
  await node(page, 'advanced').dblclick();
  // ties are carried by the notes (tie-id) and shown in the nodes, so nothing is left unsupported: the source button stays, the warning icon does not
  await expect(page.locator('#midi-gate')).toBeVisible();
  await expect(page.locator('#ro-source')).toBeVisible();
  await expect(page.locator('#ro-midi-info')).toBeHidden();
  // the retained events are not a second card on the canvas: the clip's properties have a MIDI events button that opens them
  await expect(node(page, 'midi-events-0')).toHaveCount(0);
  await node(page, 'clip').locator('.nh .t').click();
  await page.getByRole('button', { name: /^MIDI events/ }).click();
  await expect(page.locator('#props')).toContainText('tick 0 · Meta');
  await page.getByRole('button', { name: '← Back to the block' }).click();
  await expect(page.getByRole('button', { name: /^MIDI events/ })).toBeVisible();
  await expect(page.locator('#canvaswrap')).not.toHaveClass(/romode/);
}

test('advanced MIDI modules keep their event facts and allow node edits', async ({ page }) => {
  await enterAdvancedModule(page);
  await expect(node(page, 'clip')).toBeVisible();
  await expect(node(page, 'instrument')).toBeVisible();
  await expect(node(page, 'mix')).toBeVisible();
  const path = 'modules/advanced-midi.module';
  const editParameter = async (block: string, name: string, value: string) => {
    await node(page, block).click();
    const row = page.locator('#props .prop').filter({ hasText: new RegExp(`^${name}\\b`) });
    const input = row.locator('input').first();
    await expect(input).toBeEnabled();
    await input.fill(value);
    await input.press('Tab');
  };
  await node(page, 'clip').click();
  await page.locator('#props .midi-note-editor button').click();
  await page.locator('#dlg-extra textarea').fill('notes {\n  note {\n    bar 1\n    start 0\n    length 2\n    pitch 60\n    velocity 80\n  }\n}');
  await page.locator('#dlg-btns button').getByText('Save notes').click();
  await expect.poll(async () => (await api(`/api/module/doc?path=${encodeURIComponent(path)}`)).blocks.find((block: { name: string }) => block.name === 'clip').parameters.find((parameter: { name: string }) => parameter.name === 'notes').value).toBe('1:0:2:60:80');
  await page.locator('#btn-undo').click();
  await expect.poll(async () => (await api(`/api/module/doc?path=${encodeURIComponent(path)}`)).blocks.find((block: { name: string }) => block.name === 'clip').parameters.find((parameter: { name: string }) => parameter.name === 'notes').value).toBe('1:0:1:60:80');
  // the key handler ignores keys typed in a field, and Tab moves the focus to the next field
  await page.evaluate(() => (document.activeElement as HTMLElement | null)?.blur());
  // the poll above reads the server; wait until the page has applied the same answer before redoing
  await expect.poll(() => page.evaluate(async () => { const r = await fetch('/api/module/doc?path=modules/advanced-midi.module', { headers: { 'X-Ample-Token': sessionStorage.getItem('ample.token')! } }); const j = await r.json(); return (window as any).AMPLE_STATE.moduleDoc.version === j.version; })).toBe(true);
  await page.keyboard.press('Control+y');
  await expect.poll(async () => (await api(`/api/module/doc?path=${encodeURIComponent(path)}`)).blocks.find((block: { name: string }) => block.name === 'clip').parameters.find((parameter: { name: string }) => parameter.name === 'notes').value).toBe('1:0:2:60:80');
  await editParameter('instrument', 'program', '40');
  await editParameter('instrument', 'gain', '-3');
  // the mixer gains are one number field per input now: the first one makes "-3 0 0 0"
  await node(page, 'mix').click();
  const firstGain = page.getByRole('spinbutton', { name: 'input 1, dB' });
  await expect(firstGain).toBeEnabled();
  await firstGain.fill('-3');
  await firstGain.press('Tab');
  await expect(page.locator('#btn-save')).toBeEnabled();
  await page.locator('#btn-save').click();
  await expect(page.locator('#btn-save')).toBeDisabled();
  await expect.poll(async () => {
    const doc = await api(`/api/module/doc?path=${encodeURIComponent(path)}`);
    return ['clip', 'instrument', 'mix'].map((name) => doc.blocks.find((block: { name: string }) => block.name === name).parameters);
  }).toEqual([
    expect.arrayContaining([expect.objectContaining({ name: 'notes', value: '1:0:2:60:80' })]),
    expect.arrayContaining([expect.objectContaining({ name: 'program', value: '40' }), expect.objectContaining({ name: 'gain', value: '-3 dB' })]),
    expect.arrayContaining([expect.objectContaining({ name: 'gains', value: '-3 0 0 0' })]),
  ]);
  const source = await api(`/api/module/source?path=${encodeURIComponent(path)}`);
  expect(source.text).toContain('# source-event track=0 tick=0 kind=Meta');
  expect(source.text).toContain('notes {');
  expect(source.text).toContain('length 2');
  expect(source.text).toContain('midi-clip clip at 1 for 1 {');
  await expect(page.locator('#midi-gate')).toBeVisible();
  await page.locator('#crumbs .c').first().click();
  await expect(page.locator('#btn-undo')).toBeDisabled();
});

test('advanced MIDI event details open in the source editor', async ({ page }) => {
  await enterAdvancedModule(page);
  await page.locator('#ro-source').click();
  await expect.poll(() => srcText(page)).toMatch(/# source-event track=0 tick=0 kind=Meta/);
});
