import { test, expect } from '@playwright/test';
import { freshServer, openApp } from '../../web/tests/support';

// On a phone the long press that starts dragging a file also ends in a contextmenu event when the file is dropped on itself or on a folder. That is not a request for the menu.
freshServer();

test('a contextmenu event during and right after a file drag is ignored; a plain long press still opens the menu', async ({ page }) => {
  await openApp(page, { reset: false });
  const result = await page.evaluate(async () => {
    const drag = await import('/app/dragfile.js');
    let opened = 0;
    const probe = document.createElement('div');
    probe.oncontextmenu = () => { opened++; };
    document.body.append(probe);
    const fire = () => probe.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true }));
    fire();                                              // no drag: the menu is asked for
    const plain = opened;
    drag.beginFileDrag({ dataTransfer: null } as any, { path: 'a/b.mid', isDir: false });
    fire();                                              // while dragging
    const during = opened;
    drag.endFileDrag();
    fire();                                              // right after the drop on itself or on a folder
    const after = opened;
    await new Promise((resolve) => setTimeout(resolve, 1000));
    fire();                                              // a real long press later
    return { plain, during, after, later: opened };
  });
  expect(result).toEqual({ plain: 1, during: 1, after: 1, later: 2 });
});
