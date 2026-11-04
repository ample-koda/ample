// Makes the PNG sizes and the Windows .ico of the AMPLE icon from web/assets/icon/ample.svg (48 px and larger) and ample-small.svg (16, 24 and 32 px:
// the eighth note is a bold dot there, because a note does not survive a few pixels).
//   node tools/make-icons.mjs
// It draws the SVG in a headless browser (Playwright, already a dev dependency) on a transparent page and writes web/assets/icon/ample-<size>.png
// and web/assets/icon/ample.ico (PNG images inside an ICO container: 16, 24, 32, 48, 64, 128 and 256).

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from '@playwright/test';

const here = path.dirname(fileURLToPath(import.meta.url));
const dir = path.resolve(here, '..', 'web', 'assets', 'icon');
const svgFull = fs.readFileSync(path.join(dir, 'ample.svg'), 'utf8');
const svgSmall = fs.readFileSync(path.join(dir, 'ample-small.svg'), 'utf8');
const SMALL_UP_TO = 32;
const sizes = [16, 24, 32, 48, 64, 128, 256, 512];
const icoSizes = [16, 24, 32, 48, 64, 128, 256];

const browser = await chromium.launch();
const png = {};
try {
  const page = await browser.newPage({ deviceScaleFactor: 1 });
  for (const size of sizes) {
    await page.setViewportSize({ width: size, height: size });
    await page.setContent(`<style>html,body{margin:0;background:transparent}svg{display:block;width:${size}px;height:${size}px}</style>${size <= SMALL_UP_TO ? svgSmall : svgFull}`);
    png[size] = await page.screenshot({ omitBackground: true, clip: { x: 0, y: 0, width: size, height: size } });
    fs.writeFileSync(path.join(dir, `ample-${size}.png`), png[size]);
  }
} finally {
  await browser.close();
}

// ICO: a 6 byte header, a 16 byte entry per image, then the PNG files (supported since Windows Vista).
const images = icoSizes.map((size) => ({ size, data: png[size] }));
const header = Buffer.alloc(6);
header.writeUInt16LE(0, 0);
header.writeUInt16LE(1, 2);
header.writeUInt16LE(images.length, 4);
let offset = 6 + images.length * 16;
const entries = images.map((image) => {
  const entry = Buffer.alloc(16);
  entry.writeUInt8(image.size >= 256 ? 0 : image.size, 0);
  entry.writeUInt8(image.size >= 256 ? 0 : image.size, 1);
  entry.writeUInt8(0, 2);
  entry.writeUInt8(0, 3);
  entry.writeUInt16LE(1, 4);
  entry.writeUInt16LE(32, 6);
  entry.writeUInt32LE(image.data.length, 8);
  entry.writeUInt32LE(offset, 12);
  offset += image.data.length;
  return entry;
});
fs.writeFileSync(path.join(dir, 'ample.ico'), Buffer.concat([header, ...entries, ...images.map((image) => image.data)]));
console.log(`wrote ${sizes.length} PNG sizes and ample.ico (${icoSizes.join(', ')}) in ${dir}`);
