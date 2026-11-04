// Makes the PNG sizes and the Windows .ico of the .amplet file icon from web/assets/icon/amplet-file.svg (48 px and larger) and amplet-file-small.svg
// (16, 24 and 32 px: thicker strokes and a dot for the note).
//   node tools/make-file-icons.mjs
// Writes web/assets/icon/amplet-file-<size>.png and web/assets/icon/amplet-file.ico (PNG images inside an ICO container).

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from '@playwright/test';

const dir = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', 'web', 'assets', 'icon');
const full = fs.readFileSync(path.join(dir, 'amplet-file.svg'), 'utf8');
const small = fs.readFileSync(path.join(dir, 'amplet-file-small.svg'), 'utf8');
const sizes = [16, 24, 32, 48, 64, 128, 256];

const browser = await chromium.launch();
const png = {};
try {
  const page = await browser.newPage({ deviceScaleFactor: 1 });
  for (const size of sizes) {
    await page.setViewportSize({ width: size, height: size });
    await page.setContent(`<style>html,body{margin:0;background:transparent}svg{display:block;width:${size}px;height:${size}px}</style>${size <= 32 ? small : full}`);
    png[size] = await page.screenshot({ omitBackground: true, clip: { x: 0, y: 0, width: size, height: size } });
    fs.writeFileSync(path.join(dir, `amplet-file-${size}.png`), png[size]);
  }
} finally {
  await browser.close();
}

const header = Buffer.alloc(6);
header.writeUInt16LE(0, 0);
header.writeUInt16LE(1, 2);
header.writeUInt16LE(sizes.length, 4);
let offset = 6 + sizes.length * 16;
const entries = sizes.map((size) => {
  const entry = Buffer.alloc(16);
  entry.writeUInt8(size >= 256 ? 0 : size, 0);
  entry.writeUInt8(size >= 256 ? 0 : size, 1);
  entry.writeUInt16LE(1, 4);
  entry.writeUInt16LE(32, 6);
  entry.writeUInt32LE(png[size].length, 8);
  entry.writeUInt32LE(offset, 12);
  offset += png[size].length;
  return entry;
});
fs.writeFileSync(path.join(dir, 'amplet-file.ico'), Buffer.concat([header, ...entries, ...sizes.map((size) => png[size])]));
console.log(`wrote ${sizes.length} PNG sizes and amplet-file.ico in ${dir}`);
