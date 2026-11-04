import { test, expect } from '@playwright/test';
import { freshServer, openApp } from '../../web/tests/support';

const song = `composition "Local seeded MIDI preview" { ample 1  tempo 120  master-seed 17 }

midi-clip phrase {
  notes 1:0:2:60:96 1:0:2:64:96 1:0:2:67:96
  bars 2
}

midi-humanize feel {
  velocity 10
  timing 20
  from phrase.notes -> notes
}

arpeggiator run {
  rate 1/8
  pattern random
  octaves 2
  gate 80
  from feel.notes -> notes
}

synth instrument {
  wave triangle
  gain -12 dB
  from run.notes -> notes
}

master master {
  from instrument.audio -> audio
}
`;

freshServer({ song });

test('seeded MIDI effects use AMPLE effective block seeds and avoid server renders', async ({ page }) => {
  await openApp(page, { reset: false });
  const midiGraph = await page.evaluate(async () => { const { flat } = await import('/app/store.js'); const graph = flat(); return { nodes: graph.nodes.filter((node: any) => ['midi-clip', 'midi-humanize', 'arpeggiator', 'synth'].includes(node.tpl)).map((node: any) => `${node.tpl}:${node.id}:${node.params.pattern || ''}`), links: graph.links.filter((link: any) => ['phrase', 'feel', 'run'].includes(link.from)).map((link: any) => `${link.from}:${link.type}:${link.to}:${link.inName}`) }; });
  expect(midiGraph).toEqual({ nodes: ['midi-clip:phrase:', 'midi-humanize:feel:', 'arpeggiator:run:random', 'synth:instrument:'], links: ['phrase:midi:feel:notes', 'feel:midi:run:notes', 'run:midi:instrument:notes'] });
  const builds: unknown[] = [], seedBlocks: string[] = [];
  page.on('request', (request) => {
    if (request.url().includes('/api/build') && request.method() === 'POST') builds.push(request.postDataJSON());
    if (request.url().includes('/api/control/seed')) seedBlocks.push(new URL(request.url()).searchParams.get('block') || '');
  });
  await page.click('#btn-play-options');
  await page.getByRole('menuitem', { name: /Play preview/ }).click();
  await expect.poll(async () => page.evaluate(() => (window as any).AMPLE_PLAYER.state().playing), { timeout: 20000 }).toBe(true);
  await expect(page.locator('#toast')).toContainText('Local MIDI transforms: feel, run');
  expect(seedBlocks.sort()).toEqual(['feel', 'run']);
  expect(builds).toHaveLength(0);
  await page.click('#btn-stop');
});
