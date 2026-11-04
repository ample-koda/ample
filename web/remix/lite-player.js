// The player of the Output pane: a round play button and the waveform of the built sound (click or drag in it to jump), the time, and a volume.
// It drives the <audio> element of the page (which stays in the page, hidden): the page keeps calling audio.play() and audio.src, this only shows it.

const BARS = 3;   // pixels between two bars of the waveform (like the docs of AMPLE)

const clock = (seconds) => {
  const s = Math.max(0, Math.floor(Number.isFinite(seconds) ? seconds : 0));
  return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`;
};

/**
 * Puts the waveform player in place of the controls of an audio element.
 * @param {HTMLAudioElement} audio the element of the page
 */
export function createPlayer(audio) {
  audio.controls = false;
  audio.style.display = 'none';
  const host = document.createElement('div');
  host.className = 'lite-wp';
  host.innerHTML = '<button type="button" class="lite-wp-play" aria-label="Play" disabled>▶</button>'
    + '<canvas class="lite-wp-wave" role="slider" aria-label="Position in the sound" tabindex="0"></canvas>'
    + '<span class="lite-wp-time">0:00 / 0:00</span>'
    + '<button type="button" class="lite-wp-loop" aria-label="Loop" aria-pressed="false" title="Loop: play the sound again and again">🔁</button>'
    + '<button type="button" class="lite-wp-mute" aria-label="Mute">\u{1F50A}</button>'
    + '<input type="range" class="lite-wp-vol" min="0" max="1" step="0.05" value="1" aria-label="Volume">';
  audio.after(host);
  const play = host.querySelector('.lite-wp-play'), canvas = host.querySelector('canvas'), time = host.querySelector('.lite-wp-time');
  const loopBtn = host.querySelector('.lite-wp-loop'), mute = host.querySelector('.lite-wp-mute'), volume = host.querySelector('.lite-wp-vol');
  let peaks = null, frame = 0, dragging = false, token = 0;

  const duration = () => (Number.isFinite(audio.duration) && audio.duration > 0 ? audio.duration : 0);
  const position = () => (duration() ? Math.min(1, audio.currentTime / duration()) : 0);
  const playing = () => !audio.paused && !audio.ended;

  function draw() {
    const style = getComputedStyle(document.documentElement), accent = style.getPropertyValue('--acc').trim() || '#4aa3ff', dim = style.getPropertyValue('--line').trim() || '#3a4452';
    const ratio = window.devicePixelRatio || 1, width = canvas.width = Math.max(1, Math.round(canvas.clientWidth * ratio)), height = canvas.height = Math.max(1, Math.round(canvas.clientHeight * ratio));
    const ctx = canvas.getContext('2d'), at = position();
    ctx.clearRect(0, 0, width, height);
    if (!peaks) { ctx.fillStyle = dim; ctx.fillRect(0, height / 2 - 1, width, 2); }
    else {
      const count = Math.floor(width / (BARS * ratio));
      for (let k = 0; k < count; k++) {
        const x = k * BARS * ratio, from = Math.floor((k / count) * peaks.length), to = Math.max(from + 1, Math.floor(((k + 1) / count) * peaks.length));
        let top = 0; for (let i = from; i < to && i < peaks.length; i++) top = Math.max(top, peaks[i]);
        const played = x / width <= at, near = playing() && played ? Math.max(0, 1 - Math.abs(x / width - at) * 12) : 0, bar = Math.min(height, Math.max(2 * ratio, top * height * 0.9 * (1 + near * 0.35)));
        ctx.fillStyle = played ? accent : dim;
        ctx.shadowColor = accent; ctx.shadowBlur = played ? near * 14 * ratio : 0;
        ctx.fillRect(x, (height - bar) / 2, 2 * ratio, bar);
      }
    }
    ctx.shadowBlur = playing() ? 16 * ratio : 0; ctx.shadowColor = accent; ctx.fillStyle = accent;
    if (duration()) ctx.fillRect(Math.min(width - 2 * ratio, at * width), 0, 2 * ratio, height);
    time.textContent = `${clock(audio.currentTime)} / ${clock(duration())}`;
    canvas.setAttribute('aria-valuenow', String(Math.round(at * 100)));
    if (playing() || dragging) frame = requestAnimationFrame(draw);
  }
  const redraw = () => { cancelAnimationFrame(frame); draw(); };

  async function readPeaks() {
    const mine = ++token;
    peaks = null; redraw();
    if (!audio.currentSrc && !audio.src) return;
    try {
      const data = await (audio.ampleBlob ? audio.ampleBlob.arrayBuffer() : (await fetch(audio.currentSrc || audio.src)).arrayBuffer());
      const context = new (window.AudioContext || window.webkitAudioContext)();
      const buffer = await context.decodeAudioData(data);
      context.close?.();
      if (mine !== token) return;
      const left = buffer.getChannelData(0), right = buffer.numberOfChannels > 1 ? buffer.getChannelData(1) : left, count = 1200, step = Math.max(1, Math.floor(left.length / count));
      const found = new Float32Array(count); let top = 0;
      for (let k = 0; k < count; k++) { let m = 0; for (let i = k * step; i < (k + 1) * step && i < left.length; i += 4) m = Math.max(m, Math.abs(left[i]), Math.abs(right[i])); found[k] = m; top = Math.max(top, m); }
      if (top > 0) for (let k = 0; k < count; k++) found[k] /= top;
      peaks = found;
    } catch { peaks = null; }
    redraw();
  }

  const sync = () => {
    play.disabled = !(audio.currentSrc || audio.src);
    play.textContent = playing() ? '❚❚' : '▶';
    play.setAttribute('aria-label', playing() ? 'Pause' : 'Play');
    host.classList.toggle('playing', playing());
    redraw();
  };
  const seek = (event) => { const box = canvas.getBoundingClientRect(); if (duration()) audio.currentTime = Math.max(0, Math.min(1, (event.clientX - box.left) / box.width)) * duration(); redraw(); };

  play.onclick = () => { if (playing()) audio.pause(); else { if (audio.ended) audio.currentTime = 0; audio.play().catch(() => {}); } };
  canvas.onpointerdown = (event) => { dragging = true; canvas.setPointerCapture(event.pointerId); seek(event); };
  canvas.onpointermove = (event) => { if (dragging) seek(event); };
  canvas.onpointerup = () => { dragging = false; redraw(); };
  canvas.onkeydown = (event) => {
    const step = event.key === 'ArrowRight' ? 2 : event.key === 'ArrowLeft' ? -2 : 0;
    if (step) { event.preventDefault(); audio.currentTime = Math.max(0, Math.min(duration(), audio.currentTime + step)); redraw(); }
  };
  volume.oninput = () => { audio.volume = Number(volume.value); audio.muted = audio.volume === 0; mute.textContent = audio.muted ? '\u{1F507}' : '\u{1F50A}'; };
  loopBtn.onclick = () => { audio.loop = !audio.loop; loopBtn.setAttribute('aria-pressed', String(audio.loop)); loopBtn.classList.toggle('on', audio.loop); };
  mute.onclick = () => { audio.muted = !audio.muted; mute.textContent = audio.muted ? '\u{1F507}' : '\u{1F50A}'; };
  for (const type of ['play', 'pause', 'ended', 'emptied', 'loadedmetadata', 'durationchange', 'seeked', 'timeupdate']) audio.addEventListener(type, sync);
  audio.addEventListener('loadstart', () => { setTimeout(readPeaks, 0); sync(); });   // after the page has set the blob of the new sound
  window.addEventListener('resize', redraw);
  readPeaks(); sync();
}
