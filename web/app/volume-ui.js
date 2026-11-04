// The volume control next to Stop: a left and a right fader (optionally linked), each with a level meter of what is actually audible.
import { $ } from './ui.js';
import { onPlaybackVolume, outputLevels, playbackChannels, setChannelVolume, setPlaybackVolume, setVolumeLinked } from './playback-volume.js';

const FLOOR_DB = -60, TOP_DB = 3;   // the meter scale; the 1.5x fader maximum can reach +3.5 dB
const position = (amplitude) => Math.max(0, Math.min(1, (20 * Math.log10(Math.max(amplitude, 1e-6)) - FLOOR_DB) / (TOP_DB - FLOOR_DB)));

export function initVolumeUi() {
  const button = $('#btn-volume'), popup = $('#play-volume'), link = $('#play-volume-link');
  const mini = button.querySelectorAll('.vol-mini i');
  const cols = ['left', 'right'].map((side, index) => {
    const col = popup.querySelector(`.vol-col[data-side="${side}"]`);
    return { side, slider: col.querySelector('input'), label: col.querySelector('output'), meter: col.querySelector('.vol-meter'), peak: col.querySelector('.vol-peak'), mini: mini[index], shown: 0, hold: 0, holdUntil: 0, clipUntil: 0 };
  });
  const render = () => {
    const state = playbackChannels();
    cols.forEach((c) => {
      const percent = Math.round(state[c.side] * 100);
      c.slider.value = String(percent); c.slider.style.setProperty('--fill', `${percent / 1.5}%`);
      c.label.textContent = `${percent}%`;
      c.slider.setAttribute('aria-valuetext', `${percent}%`);
    });
    link.setAttribute('aria-pressed', String(state.linked));
    link.title = state.linked ? 'Linked: left and right move together (click to set them apart)' : 'Not linked: left and right are set apart (click to link)';
    const adjusted = Math.round(state.left * 100) !== 100 || Math.round(state.right * 100) !== 100;
    button.classList.toggle('volume-adjusted', adjusted);
    const summary = Math.round(state.left * 100) === Math.round(state.right * 100) ? `${Math.round(state.left * 100)}%` : `L ${Math.round(state.left * 100)}% / R ${Math.round(state.right * 100)}%`;
    button.title = adjusted ? `Opgelet: volumeaanpassing staat aan (${summary}). Dit is niet het volume van de master.` : 'Local playback volume: 100% (original output)';
    button.setAttribute('aria-label', `Local playback volume: ${summary}`);
  };
  cols.forEach((c) => { c.slider.oninput = () => setChannelVolume(c.side, Number(c.slider.value) / 100); });
  link.onclick = () => setVolumeLinked(!playbackChannels().linked);
  $('#play-volume-reset').onclick = () => setPlaybackVolume(1);
  onPlaybackVolume(render); render();

  // meters: a smooth fall-back, a peak line that holds for a second, and a red clip mark that stays a little longer
  const frame = (now) => {
    requestAnimationFrame(frame);
    if (document.hidden) return;
    const levels = outputLevels();
    cols.forEach((c) => {
      const amplitude = levels[c.side], pos = position(amplitude);
      c.shown = pos >= c.shown ? pos : Math.max(pos, c.shown - 0.02);
      if (pos >= c.hold) { c.hold = pos; c.holdUntil = now + 1000; } else if (now > c.holdUntil) c.hold = Math.max(pos, c.hold - 0.02);
      if (amplitude >= 0.995) c.clipUntil = now + 1500;
      c.mini.style.transform = `scaleY(${Math.max(0.06, c.shown)})`;
      if (popup.hidden) return;
      c.meter.style.setProperty('--level', String(c.shown));
      c.peak.style.bottom = `${c.hold * 100}%`;
      c.meter.classList.toggle('clip', now < c.clipUntil);
    });
  };
  requestAnimationFrame(frame);
}
