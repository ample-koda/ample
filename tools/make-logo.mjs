// Writes the AMPLE logo SVGs (web/assets/logo) and a PNG of each (twice the size, transparent).
//   node tools/make-logo.mjs
// The letters are single strokes (neon tubes), so the word does not depend on a font; only the line with the whole name in the lockup is text.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from '@playwright/test';

const dir = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', 'web', 'assets', 'logo') + path.sep;

// the letters as one stroke each, on a 170 high board; ox is the x offset of the word
const letters = (ox) => {
  const x = (n) => n + ox;
  return {
    A: `M${x(26)} 138 L${x(64)} 36 L${x(102)} 138`,
    M: `M${x(136)} 138 V36 L${x(178)} 102 L${x(220)} 36 V138`,
    P: `M${x(254)} 138 V36 H${x(286)} a26 26 0 0 1 0 52 H${x(254)}`,
    L: `M${x(342)} 36 V138 H${x(398)}`,
    E: `M${x(478)} 36 H${x(436)} V138 H${x(478)} M${x(436)} 87 H${x(468)}`,
    wave: `M${x(40)} 104 q6 -17 12 0 t12 0 t12 0 t12 0`,
    noteX: x(512),
  };
};

const FILTERS = `
    <filter id="glow" x="-20%" y="-40%" width="140%" height="180%">
      <feGaussianBlur in="SourceGraphic" stdDeviation="9" result="wide"/>
      <feColorMatrix in="wide" type="matrix" values="1 0 0 0 .6  0 .3 0 0 0  0 0 1 0 .5  0 0 0 .7 0" result="pink"/>
      <feGaussianBlur in="SourceGraphic" stdDeviation="2.5" result="tight"/>
      <feMerge><feMergeNode in="pink"/><feMergeNode in="tight"/><feMergeNode in="SourceGraphic"/></feMerge>
    </filter>
    <filter id="glowWhite" x="-30%" y="-60%" width="160%" height="220%">
      <feGaussianBlur stdDeviation="3.5" result="b"/>
      <feMerge><feMergeNode in="b"/><feMergeNode in="b"/><feMergeNode in="SourceGraphic"/></feMerge>
    </filter>
    <filter id="glowNote" x="-150%" y="-150%" width="400%" height="400%">
      <feGaussianBlur stdDeviation="4" result="b"/>
      <feMerge><feMergeNode in="b"/><feMergeNode in="SourceGraphic"/></feMerge>
    </filter>`;

const word = (L, y, neon) => `
  <g transform="translate(0 ${y})">
    <g fill="none" stroke="${neon}" stroke-width="15" stroke-linecap="round" stroke-linejoin="round" filter="url(#glow)">
      <path d="${L.A}"/><path d="${L.M}"/><path d="${L.P}"/><path d="${L.L}"/><path d="${L.E}"/>
    </g>
    <path d="${L.wave}" fill="none" stroke="#ffffff" stroke-width="9" stroke-linecap="round" stroke-linejoin="round" filter="url(#glowWhite)"/>
    <g filter="url(#glowNote)">
      <ellipse cx="${L.noteX}" cy="44" rx="8" ry="6" fill="#19e3c8" transform="rotate(-20 ${L.noteX} 44)"/>
      <path d="M${L.noteX + 7} 42 V12 q10 3 15 15" fill="none" stroke="#19e3c8" stroke-width="5" stroke-linecap="round" stroke-linejoin="round"/>
    </g>
  </g>`;

// 1. the word mark
{
  const L = letters(0);
  fs.writeFileSync(dir + 'ample-logo.svg', `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 560 170" width="560" height="170" role="img" aria-label="AMPLE">
  <title>AMPLE</title>
  <defs>
    <linearGradient id="neon" gradientUnits="userSpaceOnUse" x1="20" y1="0" x2="500" y2="0">
      <stop offset="0" stop-color="#2de2ff"/>
      <stop offset="1" stop-color="#ff4fd8"/>
    </linearGradient>${FILTERS}
  </defs>
  <!-- the letters are neon tubes: one stroke, round ends, cyan to magenta across the word; the crossbar of the A is the white wave of the icon; the eighth note ends the word -->${word(L, 0, 'url(#neon)')}
</svg>
`);
}

// 2. the word mark for light backgrounds: no glow, darker colours
{
  const L = letters(0);
  fs.writeFileSync(dir + 'ample-logo-light.svg', `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 560 170" width="560" height="170" role="img" aria-label="AMPLE">
  <title>AMPLE (for light backgrounds)</title>
  <defs>
    <linearGradient id="ink" gradientUnits="userSpaceOnUse" x1="20" y1="0" x2="500" y2="0">
      <stop offset="0" stop-color="#0a8fc0"/>
      <stop offset="1" stop-color="#c2208f"/>
    </linearGradient>
  </defs>
  <!-- the same letters without the glow: a darker gradient that holds on white -->
  <g fill="none" stroke="url(#ink)" stroke-width="15" stroke-linecap="round" stroke-linejoin="round">
    <path d="${L.A}"/><path d="${L.M}"/><path d="${L.P}"/><path d="${L.L}"/><path d="${L.E}"/>
  </g>
  <path d="${L.wave}" fill="none" stroke="#1a2342" stroke-width="9" stroke-linecap="round" stroke-linejoin="round"/>
  <ellipse cx="${L.noteX}" cy="44" rx="8" ry="6" fill="#0fae99" transform="rotate(-20 ${L.noteX} 44)"/>
  <path d="M${L.noteX + 7} 42 V12 q10 3 15 15" fill="none" stroke="#0fae99" stroke-width="5" stroke-linecap="round" stroke-linejoin="round"/>
</svg>
`);
}

// 3. the icon, the word and the whole name
{
  const L = letters(250);
  fs.writeFileSync(dir + 'ample-logo-lockup.svg', `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 840 220" width="840" height="220" role="img" aria-label="AMPLE, Assisted Music Programming Language Experience">
  <title>AMPLE: Assisted Music Programming Language Experience</title>
  <defs>
    <linearGradient id="tile" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#0f1226"/><stop offset="1" stop-color="#241a4a"/></linearGradient>
    <linearGradient id="neon" gradientUnits="userSpaceOnUse" x1="270" y1="0" x2="750" y2="0"><stop offset="0" stop-color="#2de2ff"/><stop offset="1" stop-color="#ff4fd8"/></linearGradient>
    <linearGradient id="neonIcon" x1="0" y1="0" x2="1" y2="0"><stop offset="0" stop-color="#2de2ff"/><stop offset="1" stop-color="#ff4fd8"/></linearGradient>${FILTERS}
    <filter id="glowIcon" x="-40%" y="-40%" width="180%" height="180%">
      <feGaussianBlur in="SourceGraphic" stdDeviation="14" result="wide"/>
      <feColorMatrix in="wide" type="matrix" values="1 0 0 0 .6  0 .3 0 0 0  0 0 1 0 .5  0 0 0 .75 0" result="pink"/>
      <feGaussianBlur in="SourceGraphic" stdDeviation="3.5" result="tight"/>
      <feMerge><feMergeNode in="pink"/><feMergeNode in="tight"/><feMergeNode in="SourceGraphic"/></feMerge>
    </filter>
    <clipPath id="clip"><rect x="8" y="8" width="240" height="240" rx="58"/></clipPath>
  </defs>

  <!-- the icon, 190 px -->
  <g transform="translate(14 14) scale(.77)">
    <rect x="8" y="8" width="240" height="240" rx="58" fill="url(#tile)"/>
    <g clip-path="url(#clip)" stroke="#ff4fd8" stroke-opacity=".45" stroke-width="2">
      <path d="M8 214H248M8 226H248M8 240H248"/>
      <path d="M128 204 L128 248M128 204 L84 248M128 204 L40 248M128 204 L8 236M128 204 L172 248M128 204 L216 248M128 204 L248 236"/>
    </g>
    <path d="M68 196 L128 58 L188 196" fill="none" stroke="url(#neonIcon)" stroke-width="24" stroke-linecap="round" stroke-linejoin="round" filter="url(#glowIcon)"/>
    <path d="M92 152 q9 -26 18 0 t18 0 t18 0 t18 0" fill="none" stroke="#fff" stroke-width="13" stroke-linecap="round" stroke-linejoin="round" filter="url(#glowWhite)"/>
    <g filter="url(#glowNote)">
      <ellipse cx="196" cy="76" rx="11" ry="8" fill="#19e3c8" transform="rotate(-20 196 76)"/>
      <path d="M205 74 V34 q14 4 20 20" fill="none" stroke="#19e3c8" stroke-width="7" stroke-linecap="round" stroke-linejoin="round"/>
    </g>
  </g>

  <!-- the word -->${word(L, 14, 'url(#neon)')}

  <!-- the whole name under the word -->
  <text x="276" y="196" fill="#9aa7bb" font-family="Segoe UI, system-ui, Arial, sans-serif" font-size="14.5" letter-spacing="2.4">ASSISTED MUSIC PROGRAMMING LANGUAGE EXPERIENCE</text>
</svg>
`);
}

// the PNGs, drawn in a headless browser
const browser = await chromium.launch();
try {
  const page = await browser.newPage({ deviceScaleFactor: 2 });
  for (const name of ['ample-logo', 'ample-logo-light', 'ample-logo-lockup']) {
    const svg = fs.readFileSync(dir + name + '.svg', 'utf8');
    const [, w, h] = /viewBox="0 0 (\d+) (\d+)"/.exec(svg);
    await page.setViewportSize({ width: Number(w), height: Number(h) });
    await page.setContent('<style>html,body{margin:0;background:transparent}svg{display:block}</style>' + svg);
    fs.writeFileSync(dir + name + '.png', await page.screenshot({ omitBackground: true, clip: { x: 0, y: 0, width: Number(w), height: Number(h) } }));
  }
} finally {
  await browser.close();
}
console.log('wrote the logo SVGs and PNGs in ' + dir);
