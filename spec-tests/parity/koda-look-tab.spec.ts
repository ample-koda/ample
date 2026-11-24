import { test, expect } from '@playwright/test';
import { freshServer, openApp } from '../../web/tests/support';

// A look from Koda at a tab stays while the tabs are switched (that is what is done with a tab), and a look at a file row points at that row and not at a tab that has the same path.
freshServer({});

test('a look at a tab does not end when another block is chosen, and a look at a block does', async ({ page }) => {
  await page.setViewportSize({ width: 1300, height: 900 });
  await openApp(page, { reset: false });
  const result = await page.evaluate(async () => {
    const gaze = await import('/app/koda-gaze.js');
    const store = await import('/app/store.js');
    const tab = document.querySelector('#tabbar .tab[data-path]') as HTMLElement | null;
    const nodes = [...document.querySelectorAll('.node[data-id]')] as HTMLElement[];
    if (!tab || nodes.length < 2) return { error: `tab ${!!tab}, nodes ${nodes.length}` };
    const box = tab.getBoundingClientRect();
    const target = gaze.targetAt(box.x + box.width / 2, box.y + box.height / 2);
    if (target?.kind !== 'tab') return { error: 'the target is ' + target?.kind };
    gaze.lookAt(target, false);
    const before = gaze.looksCount();
    store.S.sel = nodes[0].dataset.id;   // another block is chosen
    await new Promise((resolve) => setTimeout(resolve, 900));   // longer than the fade of a look that ends
    const afterTab = gaze.looksCount();
    gaze.clearLook();
    const first = nodes[0].getBoundingClientRect();
    const block = gaze.targetAt(first.x + first.width / 2, first.y + first.height / 2);
    store.S.sel = nodes[0].dataset.id;
    gaze.lookAt(block, false);
    store.S.sel = nodes[1].dataset.id;
    await new Promise((resolve) => setTimeout(resolve, 900));
    return { before, afterTab, blockKind: block?.kind, afterBlock: gaze.looksCount() };
  });
  expect((result as any).error).toBeUndefined();
  expect((result as any).before).toBe(1);
  expect((result as any).afterTab).toBe(1);
  expect((result as any).blockKind).toBe('block');
  expect((result as any).afterBlock).toBe(0);
});
