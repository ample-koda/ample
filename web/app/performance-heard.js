// What the DJ heard, kept while the set is played ("heard = saved"): the sound that leaves the output of the page (after the safety stage, before the listening volume) as 16-bit stereo blocks,
// and the peak of the sum before the safety stage. When the set is saved as a song the saved song is rendered and compared bar by bar with this
// recording; with Keep it is also saved as a WAV next to the song.
// A ScriptProcessor does the listening in: it is the one way that works in every browser the page runs in without a separate worklet file.
import { playbackTap } from './playback-volume.js';

/** The recording stops growing after this many minutes (16-bit stereo is about 10 MB a minute); the peaks go on. */
export const MAX_MINUTES = 20;

export function createHeardRecorder(ctx, { maxMinutes = MAX_MINUTES } = {}) {
  if (typeof ctx.createScriptProcessor !== 'function' || typeof ctx.createChannelMerger !== 'function') return null;
  const { sum, heard } = playbackTap(ctx);
  const sumSplit = ctx.createChannelSplitter(2), heardSplit = ctx.createChannelSplitter(2), merge = ctx.createChannelMerger(4);
  sum.connect(sumSplit); heard.connect(heardSplit);
  sumSplit.connect(merge, 0, 0); sumSplit.connect(merge, 1, 1); heardSplit.connect(merge, 0, 2); heardSplit.connect(merge, 1, 3);
  const processor = ctx.createScriptProcessor(4096, 4, 1), quiet = ctx.createGain();
  quiet.gain.value = 0;   // the processor only runs while it is connected on to the output; it adds nothing to it
  merge.connect(processor); processor.connect(quiet); quiet.connect(ctx.destination);

  let chunks = [], peaks = [], firstTime = null, samples = 0, full = false, running = true;
  const limit = maxMinutes * 60 * ctx.sampleRate;
  const int16 = (data) => { const out = new Int16Array(data.length); for (let i = 0; i < data.length; i++) { const v = data[i];
    out[i] = v <= -1 ? -32768 : v >= 1 ? 32767 : Math.round(v * 32767); } return out; };
  processor.onaudioprocess = (event) => {
    if (!running) return;
    const input = event.inputBuffer, time = event.playbackTime ?? ctx.currentTime;
    const a = input.getChannelData(0), b = input.getChannelData(1);
    let peak = 0;
    for (let i = 0; i < a.length; i++) { const x = Math.abs(a[i]), y = Math.abs(b[i]); if (x > peak) peak = x; if (y > peak) peak = y; }
    peaks.push({ time, peak });
    if (full) return;
    if (firstTime === null) firstTime = time;
    chunks.push({ l: int16(input.getChannelData(2)), r: int16(input.getChannelData(3)), scale: 1 / 32767 });
    samples += a.length;
    if (samples >= limit) full = true;
  };

  return {
    sampleRate: ctx.sampleRate,
    /** Starts the recording again from the next block (the take was restarted). */
    restart() { chunks = []; peaks = []; firstTime = null; samples = 0; full = false; },
    /** Lets go of the output; returns what was heard: { chunks, peaks, firstTime (the clock time of the first sample), sampleRate, full }. */
    stop() {
      running = false;
      processor.onaudioprocess = null;
      for (const node of [sum, heard]) { try { node.disconnect(sumSplit); } catch { /* not connected to it */ } try { node.disconnect(heardSplit); } catch { /* not connected to it */ } }
      for (const node of [sumSplit, heardSplit, merge, processor, quiet]) { try { node.disconnect(); } catch { /* disconnected already */ } }
      return { chunks, peaks, firstTime, sampleRate: ctx.sampleRate, full };
    },
    get seconds() { return samples / ctx.sampleRate; },
  };
}

/** A WAV file of the recording (16-bit stereo), as a Blob. */
export function heardWav({ chunks, sampleRate }) {
  const frames = chunks.reduce((n, c) => n + c.l.length, 0), data = frames * 4;
  const header = new ArrayBuffer(44), view = new DataView(header);
  const write = (offset, text) => [...text].forEach((character, index) => view.setUint8(offset + index, character.charCodeAt(0)));
  write(0, 'RIFF'); view.setUint32(4, 36 + data, true); write(8, 'WAVE'); write(12, 'fmt '); view.setUint32(16, 16, true);
  view.setUint16(20, 1, true); view.setUint16(22, 2, true); view.setUint32(24, sampleRate, true); view.setUint32(28, sampleRate * 4, true); view.setUint16(32, 4, true); view.setUint16(34, 16, true);
  write(36, 'data'); view.setUint32(40, data, true);
  const parts = [header];
  for (const c of chunks) {
    const out = new Int16Array(c.l.length * 2);
    for (let i = 0; i < c.l.length; i++) { out[i * 2] = c.l[i]; out[i * 2 + 1] = c.r[i]; }
    parts.push(out.buffer);
  }
  return new Blob(parts, { type: 'audio/wav' });
}
