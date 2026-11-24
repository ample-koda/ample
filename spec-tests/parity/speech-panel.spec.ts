import { test, expect } from '@playwright/test';
import { api, freshServer, node, openApp } from '../../web/tests/support';

// The speech panel: text and phonemes in one multiline field, a mode switch, and only the controls of the chosen mode.
const SONG = `composition "demo" { ample 1  tempo 100  master-seed 1 }
speech voice at 1 for 2 {
  text "Hello there"
}
master master {
  from voice.audio -> audio
}
`;
freshServer({ song: SONG });

const docText = async () => ((await api('/api/doc?path=song.amplet')) as { text: string }).text;

test('the speech panel writes text, mode and phonemes to the source', async ({ page }) => {
  await openApp(page, { reset: false });
  await node(page, 'voice').getByRole('button', { name: 'Advanced', exact: true }).evaluate((b: HTMLElement) => b.click());
  const dialog = page.locator('#studio[open]');
  const area = dialog.getByRole('textbox', { name: 'Text to speak' });
  await expect(area).toHaveValue('Hello there');
  await expect(dialog.getByText('Voice pitch', { exact: true })).toBeVisible();
  await expect(dialog.getByText('Duration', { exact: true })).toHaveCount(0);

  await area.fill('Good\nmorning "friend"');
  await area.blur();
  await expect.poll(docText).toContain(String.raw`text "Good\nmorning \"friend\""`);

  // phonemes replace the text in the source (the renderer refuses both); the typed text comes back when they are switched off
  await dialog.getByLabel('Write phonemes instead of text').check();
  const phonemes = dialog.getByRole('textbox', { name: 'Phonemes to speak' });
  await expect(phonemes).toHaveValue('');
  await phonemes.fill('HH AH0 L OW1');
  await phonemes.blur();
  await expect.poll(docText).toContain('phonemes "HH AH0 L OW1"');
  await expect.poll(async () => /^\s+text /m.test(await docText())).toBe(false);   // the text line is removed, not emptied
  await dialog.getByLabel('Write phonemes instead of text').uncheck();
  await expect(dialog.getByRole('textbox', { name: 'Text to speak' })).toHaveValue('Good\nmorning "friend"');
  await expect.poll(docText).toContain(String.raw`text "Good\nmorning \"friend\""`);
  await expect.poll(async () => /phonemes/.test(await docText())).toBe(false);

  // words mode: the controls of that mode, no phonemes
  await dialog.getByRole('combobox', { name: 'Mode' }).selectOption('words');
  await expect.poll(docText).toMatch(/mode "words"/);
  await expect(dialog.getByText('Duration', { exact: true })).toBeVisible();
  await expect(dialog.getByText('Voice pitch', { exact: true })).toHaveCount(0);
  await expect(dialog.getByLabel('Write phonemes instead of text')).toBeHidden();
  await expect(dialog.getByText('Fit ceiling')).toHaveCount(0);
  await dialog.getByRole('combobox', { name: 'Duration' }).selectOption('fit');
  await expect(dialog.getByText('Fit ceiling')).toBeVisible();
  // the cases of this file share one song: leave it in text mode, as the next case expects
  await dialog.getByRole('combobox', { name: 'Mode' }).selectOption('text');
  await expect.poll(docText).toMatch(/mode "text"/);
});

test('the speech panel checks flite phonemes before the build does', async ({ page }) => {
  await openApp(page, { reset: false });
  await node(page, 'voice').getByRole('button', { name: 'Advanced', exact: true }).evaluate((b: HTMLElement) => b.click());
  const dialog = page.locator('#studio[open]');
  await dialog.getByRole('combobox', { name: 'Mode' }).selectOption('text');   // whatever an earlier case left in the song
  await dialog.getByRole('combobox', { name: 'Voice' }).selectOption('flite');
  await expect.poll(docText).toContain('engine "flite"');

  // the phonemes field is offered for flite too, with its own hint: phones sound flatter than text
  await dialog.getByLabel('Write phonemes instead of text').check();
  const phonemes = dialog.getByRole('textbox', { name: 'Phonemes to speak' });
  await expect(dialog.getByText(/flatter than text/)).toBeVisible();

  // an unknown phoneme is named, with the valid ones, while typing; nothing is written for a bad field until it is left
  const note = dialog.getByRole('status').filter({ hasText: 'not a phoneme' });
  await phonemes.fill('HH XX1 L');
  await expect(note).toBeVisible();
  await expect(note).toContainText("'XX1'");
  // the valid phonemes are one closed line that opens on a click (40 symbols are too long for the panel), and a tap on a phoneme inserts it
  const list = dialog.locator('details.speech-phone-list');
  await expect(list.locator('summary')).toHaveText(/^Valid phonemes \(\d+\)$/);
  await expect(list).toHaveJSProperty('open', false);
  await list.locator('summary').click();
  await expect(list).toHaveJSProperty('open', true);
  await expect(list.getByRole('button', { name: 'Insert phoneme IY', exact: true })).toBeVisible();
  await expect(phonemes).toHaveAttribute('aria-invalid', 'true');
  await phonemes.fill('T1');
  await expect(dialog.getByRole('status').filter({ hasText: 'only valid on vowels' })).toBeVisible();

  // a good string clears the note and is written as the phonemes line; the build then judges the rest
  await phonemes.fill('HH AH0 L OW1');
  await expect(dialog.getByRole('status').filter({ hasText: /phoneme|vowels/ })).toBeHidden();
  await expect(phonemes).toHaveAttribute('aria-invalid', 'false');
  await phonemes.blur();
  await expect.poll(docText).toContain('phonemes "HH AH0 L OW1"');

  // another engine has its own table: the note goes and the hint loses the flite sentence
  await dialog.getByRole('combobox', { name: 'Voice' }).selectOption('humanico');
  await expect(dialog.getByText(/flatter than text/)).toHaveCount(0);
  await phonemes.fill('HH XX1 L');
  await expect(dialog.getByRole('status').filter({ hasText: 'not a phoneme' })).toBeHidden();
});

test('a knob that the block does not write shows what the block does, and the voice choice shows its whole label', async ({ page }) => {
  await openApp(page, { reset: false });
  await node(page, 'voice').getByRole('button', { name: 'Advanced', exact: true }).evaluate((b: HTMLElement) => b.click());
  const dialog = page.locator('#studio[open]');
  const readout = (name: string) => dialog.getByRole('button', { name: `Edit ${name} value` });
  // the song writes none of them: the knobs start from the defaults of the block, not from the bottom of their range
  await expect(readout('Voice pitch')).toHaveText('105 Hz');
  await expect(readout('Rate')).toHaveText('1');
  await expect(readout('Gain')).toHaveText('0 dB');
  const voice = dialog.getByRole('combobox', { name: 'Voice' });
  await voice.selectOption('flite');
  await expect(readout('Voice pitch')).toHaveText('95 Hz');
  await expect(readout('Pitch range')).toHaveText('11 Hz');
  expect(await docText()).not.toMatch(/^\s+gain /m);   // looking at the panel writes nothing

  // the selected label is not cut off at a wide or at a narrow window
  for (const size of [{ width: 1280, height: 900 }, { width: 390, height: 844 }]) {
    await page.setViewportSize(size);
    const cut = await voice.evaluate((select: HTMLSelectElement) => {
      const style = getComputedStyle(select);
      const canvas = document.createElement('canvas').getContext('2d')!;
      canvas.font = `${style.fontSize} ${style.fontFamily}`;
      const text = select.selectedOptions[0].textContent || '';
      const room = select.clientWidth - parseFloat(style.paddingLeft) - parseFloat(style.paddingRight) - 24;   // the arrow
      return canvas.measureText(text).width - room;
    });
    expect(cut, `the voice label is cut off at ${size.width}px`).toBeLessThanOrEqual(0);
  }
});

test('the flite panel offers the leading silence, the pause length and the brightness, and every engine the loudness choice', async ({ page }) => {
  await openApp(page, { reset: false });
  await node(page, 'voice').getByRole('button', { name: 'Advanced', exact: true }).evaluate((b: HTMLElement) => b.click());
  const dialog = page.locator('#studio[open]');
  // another engine (the tests of this file share one song, so say which): the shaping controls are not there, the loudness choice is
  await dialog.getByRole('combobox', { name: 'Voice' }).selectOption('humanico');
  await expect(dialog.getByText('Pause length', { exact: true })).toHaveCount(0);
  await expect(dialog.getByRole('combobox', { name: 'Loudness' })).toBeVisible();

  await dialog.getByRole('combobox', { name: 'Voice' }).selectOption('flite');
  await expect(dialog.getByText('Pause length', { exact: true })).toBeVisible();
  await expect(dialog.getByText('Brightness', { exact: true })).toBeVisible();
  await dialog.getByRole('combobox', { name: 'Leading silence' }).selectOption('on');
  await expect.poll(docText).toContain('lead_trim "on"');
  await dialog.getByRole('combobox', { name: 'Loudness' }).selectOption('matched');
  await expect.poll(docText).toContain('level "matched"');

  // the shaping belongs to flite: the renderer refuses it next to another engine, so the lines go with the engine; the loudness choice stays
  await dialog.getByRole('combobox', { name: 'Voice' }).selectOption('humanico');
  await expect.poll(async () => /lead_trim|pause_scale|brightness/.test(await docText())).toBe(false);
  await expect.poll(docText).toContain('level "matched"');
  await expect(dialog.getByText('Pause length', { exact: true })).toHaveCount(0);
});

test('a tap on a phoneme inserts it at the cursor, and the choices and knobs sit in rows', async ({ page }) => {
  await openApp(page, { reset: false });
  await node(page, 'voice').getByRole('button', { name: 'Advanced', exact: true }).evaluate((b: HTMLElement) => b.click());
  const dialog = page.locator('#studio[open]');
  const voice = dialog.getByRole('combobox', { name: 'Voice' });
  const mode = dialog.getByRole('combobox', { name: 'Mode' });
  // Voice and Mode are one row (the same top coordinate), the rate sits next to the voice pitch for the engines that have no pitch range
  await mode.selectOption('text');
  await voice.selectOption('humanico');
  const top = (locator: ReturnType<typeof dialog.locator>) => locator.evaluate((item) => Math.round(item.getBoundingClientRect().top));
  // the panel is built again after every change of the source, so a measurement polls until the two things are in the new panel and in one row
  const sameRow = (a: ReturnType<typeof dialog.locator>, b: ReturnType<typeof dialog.locator>) => expect.poll(async () => {
    const [x, y] = await Promise.all([top(a).catch(() => -1), top(b).catch(() => -2)]);
    return x > 0 && x === y;
  }).toBe(true);
  const readout = (name: string) => dialog.getByRole('button', { name: `Edit ${name} value` });
  await expect(readout('Rate')).toBeVisible();   // the panel is built again after a change: wait for the new one before measuring
  await sameRow(voice, mode);
  await sameRow(readout('Rate'), readout('Voice pitch'));
  await voice.selectOption('flite');
  await expect(readout('Pitch range')).toBeVisible();
  await sameRow(readout('Pitch range'), readout('Voice pitch'));
  await mode.selectOption('words');
  await expect(dialog.getByText('Duration', { exact: true })).toBeVisible();
  await sameRow(voice, mode);   // the Mode choice is made again after a change and stays in its place
  await expect(readout('Rate')).toBeVisible();
  await mode.selectOption('text');
  await expect(readout('Pitch range')).toBeVisible();

  // a tap on a phoneme inserts it at the cursor with a space on the side where it is needed
  await dialog.getByLabel('Write phonemes instead of text').check();
  const phonemes = dialog.getByRole('textbox', { name: 'Phonemes to speak' });
  await phonemes.fill('HH L');
  await dialog.locator('details.speech-phone-list summary').click();
  // the cursor is put like a person does it: click in the field, then the keys; the tap on a key of the palette leaves the field
  await phonemes.click();
  await phonemes.press('Home');
  await phonemes.press('ArrowRight');
  await phonemes.press('ArrowRight');   // after HH
  await dialog.getByRole('button', { name: 'Insert phoneme AH', exact: true }).click();
  await expect(phonemes).toHaveValue('HH AH L');
  await phonemes.click();
  await phonemes.press('End');
  await dialog.getByRole('button', { name: 'Insert phoneme OW', exact: true }).click();
  await expect(phonemes).toHaveValue('HH AH L OW');
  // the next tap goes behind the phoneme that was inserted, also though the panel was built again in between
  await dialog.getByRole('button', { name: 'Insert phoneme T', exact: true }).click();
  await expect(phonemes).toHaveValue('HH AH L OW T');
  await expect.poll(docText).toContain('phonemes "HH AH L OW T"');   // an insert is written like a typed change
  await expect(dialog.getByRole('status').filter({ hasText: /phoneme|vowels/ })).toBeHidden();
  // the palette is for flite phonemes only
  await voice.selectOption('humanico');
  await expect(dialog.locator('details.speech-phone-list')).toBeHidden();
});

test('the voices slt, rms and awb are in the voice choice: they write the voice, show the pitch of their recording, offer no phonemes and sing in words mode @quarantine', async ({ page }) => {
  await openApp(page, { reset: false });
  await node(page, 'voice').getByRole('button', { name: 'Advanced', exact: true }).evaluate((b: HTMLElement) => b.click());
  const dialog = page.locator('#studio[open]');
  const readout = (name: string) => dialog.getByRole('button', { name: `Edit ${name} value` });
  const voice = dialog.getByRole('combobox', { name: 'Voice' });
  await voice.selectOption('flite');
  await expect.poll(docText).toContain('engine "flite"');
  await expect(readout('Voice pitch')).toHaveText('95 Hz');
  await voice.selectOption('flite:slt');
  await expect.poll(docText).toContain('voice "slt"');
  // the pitch of the other voice is not carried over: the voice keeps the pitch and the range of its own recording
  // the voice and the removal of the pitch are two edits: the second is waited for
  await expect.poll(docText).not.toMatch(/pitch_hz/);
  await expect(readout('Voice pitch')).toHaveText('172 Hz');
  await expect(readout('Pitch range')).toHaveText('27 Hz');
  await expect(dialog.getByText('Flite slt is a statistical US English voice')).toBeVisible();
  await expect(dialog.getByText('Write phonemes instead of text')).toBeHidden();   // no phonemes for these voices
  await expect(dialog.getByRole('combobox', { name: 'Mode' }).locator('option')).toHaveCount(2);
  // words mode: each voice names the range that it sings
  await dialog.getByRole('combobox', { name: 'Mode' }).selectOption('words');
  await expect.poll(docText).toContain('mode "words"');
  await expect(dialog.getByText('Flite sings a pitch from 74 to 650 Hz')).toBeVisible();
  await voice.selectOption('flite:awb');
  await expect(dialog.getByText('Flite sings a pitch from 114 to 486 Hz')).toBeVisible();
  await voice.selectOption('flite:rms');
  await expect(dialog.getByText('Flite sings a pitch from 55 to 450 Hz')).toBeVisible();
  await voice.selectOption('flite:slt');
  await dialog.getByRole('combobox', { name: 'Mode' }).selectOption('text');
  await expect.poll(docText).toContain('mode "text"');
  await voice.selectOption('flite:awb');
  await expect.poll(docText).toContain('voice "awb"');
  await expect(readout('Voice pitch')).toHaveText('132 Hz');
  await voice.selectOption('flite:rms');
  await expect(readout('Voice pitch')).toHaveText('98 Hz');
  // back to kal16: the voice line goes (kal16 is what a block without one has), and the pitch of kal16 is shown again
  await voice.selectOption('flite');
  await expect.poll(docText).not.toContain('voice "rms"');
  await expect(readout('Voice pitch')).toHaveText('95 Hz');
  await expect(dialog.getByText('Write phonemes instead of text')).toBeVisible();
  await voice.selectOption('humanico');
  await expect.poll(docText).toContain('engine "humanico"');
  expect(await docText()).not.toMatch(/voice "/);
});
