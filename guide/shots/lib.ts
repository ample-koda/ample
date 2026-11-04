// Shared by the screenshot programs of the guide: opening a real AMPLE on a clean project, the numbered red frames, and a few sounds made from maths so
// that no recording of anyone is needed (a kick, a snare, a hat and a soft pad).
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { expect, Page } from '@playwright/test';
import { startServer } from '../../web/tests/server';

const here = path.dirname(fileURLToPath(import.meta.url));
export const OUT = path.resolve(here, '..', 'img');
fs.mkdirSync(OUT, { recursive: true });

const RATE = 44100;
function wav(samples: number[]): Buffer {
  const b = Buffer.alloc(44 + samples.length * 2);
  b.write('RIFF', 0); b.writeInt32LE(36 + samples.length * 2, 4); b.write('WAVEfmt ', 8); b.writeInt32LE(16, 16); b.writeInt16LE(1, 20); b.writeInt16LE(1, 22);
  b.writeInt32LE(RATE, 24); b.writeInt32LE(RATE * 2, 28); b.writeInt16LE(2, 32); b.writeInt16LE(16, 34); b.write('data', 36); b.writeInt32LE(samples.length * 2, 40);
  samples.forEach((s, i) => b.writeInt16LE(Math.max(-32768, Math.min(32767, Math.round(s))), 44 + i * 2));
  return b;
}
let seed = 12345;
const noise = () => { seed = (seed * 1664525 + 1013904223) >>> 0; return seed / 2 ** 31 - 1; };
const kick = () => { let phase = 0; return wav([...Array(Math.round(RATE * 0.35))].map((_, i) => { const t = i / RATE; phase += 2 * Math.PI * (45 + 90 * Math.exp(-t * 30)) / RATE; return 26000 * Math.exp(-t * 9) * Math.sin(phase); })); };
const snare = () => wav([...Array(Math.round(RATE * 0.25))].map((_, i) => { const t = i / RATE; return 14000 * Math.exp(-t * 22) * noise() + 9000 * Math.exp(-t * 28) * Math.sin(2 * Math.PI * 190 * t); }));
const hat = () => { let prev = 0; return wav([...Array(Math.round(RATE * 0.08))].map((_, i) => { const t = i / RATE; const n = noise(); const hp = n - prev; prev = n; return 9000 * Math.exp(-t * 60) * hp; })); };
const pad = () => wav([...Array(Math.round(RATE * 4))].map((_, i) => { const t = i / RATE; return 4500 * (Math.sin(2 * Math.PI * 220 * t) + Math.sin(2 * Math.PI * 261.63 * t) + Math.sin(2 * Math.PI * 329.63 * t)) / 3 * Math.min(1, t * 4) * Math.min(1, (4 - t) * 4); }));

/** The sounds of the guide: samples/kit (kick, snare, hat) and samples/loops/pad.wav, in a project folder. */
export function writeSounds(folder: string) {
  fs.mkdirSync(path.join(folder, 'samples/kit'), { recursive: true });
  fs.mkdirSync(path.join(folder, 'samples/loops'), { recursive: true });
  for (const [name, data] of [['kick', kick()], ['snare', snare()], ['hat', hat()]] as const) fs.writeFileSync(path.join(folder, 'samples/kit', `${name}.wav`), data);
  fs.writeFileSync(path.join(folder, 'samples/loops/pad.wav'), pad());
}

type Mark = { selector: string; n: number };

/** Draws numbered red frames over elements; take the picture, then call unmark. */
export async function annotate(page: Page, marks: Mark[]) {
  await page.evaluate((items) => {
    const layer = document.createElement('div');
    layer.id = 'guide-marks';
    layer.style.cssText = 'position:fixed;inset:0;pointer-events:none;z-index:99999';
    for (const { selector, n } of items) {
      const el = document.querySelector(selector);
      if (!el) continue;
      const r = el.getBoundingClientRect();
      const box = document.createElement('div');
      box.style.cssText = `position:fixed;left:${r.left - 2}px;top:${r.top - 2}px;width:${r.width + 4}px;height:${r.height + 4}px;border:2px solid #ff4d4f;border-radius:6px;box-shadow:0 0 0 1px rgba(0,0,0,.4)`;
      const badge = document.createElement('div');
      badge.textContent = String(n);
      badge.style.cssText = `position:absolute;left:${Math.max(2, r.left - 12) - (r.left - 2)}px;top:${Math.max(2, r.top - 12) - (r.top - 2)}px;`;
      badge.style.cssText += 'width:22px;height:22px;border-radius:50%;background:#ff4d4f;color:#fff;font:700 13px/22px system-ui;text-align:center;box-shadow:0 1px 4px rgba(0,0,0,.5)';
      box.append(badge);
      layer.append(box);
    }
    document.body.append(layer);
  }, marks);
}
export const unmark = (page: Page) => page.evaluate(() => document.getElementById('guide-marks')?.remove());

/** Starts a real AMPLE on a clean project with this song (and the sounds and extra text files) and opens it in the page. */
export async function open(page: Page, song: string, files: Record<string, string> = {}, sounds = false) {
  const server = await startServer({ song, files, prepare: sounds ? (_cli, folder) => writeSounds(folder) : undefined });
  await page.goto(server.launch);
  await page.waitForSelector('body[data-ready="1"]');
  await page.addStyleTag({ content: '#toast{display:none!important}' });   // the pictures show the program, not its messages
  await page.waitForTimeout(600);
  return server;
}
export const fit = async (page: Page) => { await page.getByRole('button', { name: 'Fit', exact: true }).click(); await page.waitForTimeout(400); };
export const built = async (page: Page) => { await page.click('#btn-build'); await expect(page.locator('#btxt')).toHaveText('idle', { timeout: 120000 }); await page.waitForTimeout(900); };
export const shot = (page: Page, name: string, clip?: { x: number; y: number; width: number; height: number }) => page.screenshot({ path: path.join(OUT, `${name}.png`), clip });
export const boxOf = async (page: Page, selector: string, pad = 0) => { const b = (await page.locator(selector).boundingBox())!; return { x: Math.max(0, b.x - pad), y: Math.max(0, b.y - pad), width: b.width + 2 * pad, height: b.height + 2 * pad }; };
