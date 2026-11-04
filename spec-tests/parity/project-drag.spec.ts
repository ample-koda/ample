import { test, expect } from '@playwright/test';
import { freshServer, openApp } from '../../web/tests/support';

freshServer();

test('project audio can be dragged onto the canvas and desktop files upload into the panel', async ({ page }) => {
  await openApp(page, { reset: false });
  await page.evaluate(async () => (window as Window & { AMPLE_API: { uploadProject: (file: File, dir: string, conflict: string) => Promise<unknown> } }).AMPLE_API.uploadProject(new File([new Uint8Array([0, 1, 2])], 'sample.wav'), '', 'refuse'));
  await page.getByRole('button', { name: 'Project', exact: true }).click();
  const source = page.locator('.project-tile').filter({ hasText: 'sample.wav' });
  await expect(source).toBeVisible();
  await source.dragTo(page.locator('#cv'));
  // the new block is named after the file and is an import-audio block
  await expect(page.locator('#toast')).toContainText('Added sample from sample.wav');
  await expect(page.locator('.node[data-id="sample"]')).toContainText(/import-audio/i);

  const upload = page.locator('#b-project input[type=file]');
  await upload.setInputFiles({ name: 'dropped.wav', mimeType: 'audio/wav', buffer: Buffer.from([1, 2, 3]) });
  await expect(page.locator('.project-tile').filter({ hasText: 'dropped.wav' })).toBeVisible();
});

test('dropping desktop files onto the Project panel uploads them with visible progress', async ({ page }) => {
  await openApp(page, { reset: false });
  await page.getByRole('button', { name: 'Project', exact: true }).click();
  await page.locator('#b-project').evaluate((panel) => {
    const transfer = new DataTransfer();
    transfer.items.add(new File([new Uint8Array([1, 2, 3])], 'panel-drop.wav', { type: 'audio/wav' }));
    panel.dispatchEvent(new DragEvent('drop', { bubbles: true, cancelable: true, dataTransfer: transfer }));
  });
  await expect(page.locator('.project-tile').filter({ hasText: 'panel-drop.wav' })).toBeVisible();
  await expect(page.locator('.project-upload-row').filter({ hasText: 'panel-drop.wav' })).toContainText('Complete');
});
