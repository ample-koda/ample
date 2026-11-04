// The lane of a control in the arrangement: the curve of its latest take, drawn on the bars of the song.
import { api } from './api.js';
import { controlAt } from '../lib/koda-control.mjs';
import { playerState } from './player.js';
import { setKodaControl } from './koda-vibe.js';
import { barBeats } from '../lib/meter.mjs';

/**
 * Draws the curve of a control take.
 * @param {HTMLCanvasElement} canvas The lane (as wide as the song).
 * @param {string} take The take id.
 * @param {number} barWidth The width of a bar in pixels.
 * @param {string} color The colour of the line.
 * @returns {Promise<void>} Resolves when drawn (a take without a curve leaves the lane empty).
 */
export async function drawControl(canvas, take, barWidth, color) {
  let curve;
  try { curve = await api.get(`/api/takes/${encodeURIComponent(take)}/control?points=1500`); } catch { return; }
  const ctx = canvas.getContext('2d');
  const h = canvas.height;
  const values = curve.values || [];
  if (!values.length) return;
  // the lane shows the range the curve really has (a general control: 0 to 1000)
  let low = 0, high = 1000;
  if (curve.unit) { low = Math.min(...values); high = Math.max(...values); if (high === low) high = low + 1; }
  const secondsPerBar = barBeats() * 60 / (Number(canvas.dataset.tempo) || 120);
  const pxPerSecond = barWidth / secondsPerBar;
  const x0 = (curve.firstBar - 1) * barWidth;
  ctx.clearRect(0, 0, canvas.width, h);
  ctx.strokeStyle = color; ctx.lineWidth = 1.5; ctx.beginPath();
  values.forEach((v, i) => {
    const x = x0 + (i / curve.rate) * pxPerSecond;
    const y = h - 2 - ((v - low) / (high - low)) * (h - 4);
    if (i === 0) ctx.moveTo(x, y); else ctx.lineTo(x, y);
  });
  ctx.stroke();
  // Koda sways a little more while this control is high (only while this take plays)
  setKodaControl(() => (playerState().playing ? controlAt(curve, playerState().time, secondsPerBar) : 0));
  canvas.title = curve.unit ? `Control in ${curve.unit}` : 'Control: a level from 0 to 1';
}
