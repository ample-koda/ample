// Hearing an automation curve while it is being edited (plan automation-preview): a DRAFT preview builds the song, or a window of bars around the point, with the draft curve
// applied exactly as the build would (the song is not changed: the server replaces the one parameter in its own copy), and LIVE runs the effect in the browser on the sound that
// feeds the block and follows the curve at the playback position, so a dragged point changes the sound at once. Live is an approximation (the browser's effects, not the engine's).
// @ample:automation
import { api } from './api.js';
import { S, flat } from './store.js';
import { play, playLivePreview, playerState, seekFraction, setLoop, setLoopRegion, stop, updateLivePreview } from './player.js';
import { fetchTakeAudio, liveEffectGraph } from './studio/preview/audio.js';
import { AUTOMATABLE, automationValueAt, liveSupported, liveSupportedText, previewWindow } from '../lib/automation-model.mjs';
import { secondsPerBar, songBar } from '../lib/block-activity.mjs';

const unquote = (text) => String(text ?? '').trim().replace(/^"|"$/g, '');

/**
 * block: the block id; type and name: its type and the automated parameter; tempo; totalBars: the length of the song in bars (for the window);
 * getPoints(): the points of the draft curve now; getText(): the draft as the text that would be written; onStatus(text).
 */
export function createAutomationPreview({ block, type, name, tempo, totalBars, getPoints, getText, onStatus = () => {} }) {
  let mode = null, generation = 0, ticker = 0;
  const range = AUTOMATABLE[type]?.[name] ?? {};

  /** The take that feeds the block through its `in` input in the current build, or null. */
  const inputTake = () => {
    const link = flat().links.find((l) => l.to === block && l.inName === 'in' && !l.disabled);
    return link ? S.build?.takes?.[link.from] ?? null : null;
  };

  /** Whether Live can be offered, and when not, why. */
  function liveState() {
    if (!liveSupported(type, name)) return { ok: false, reason: `Live works for: ${liveSupportedText()}. Use Preview for this one.` };
    if (!inputTake()) return { ok: false, reason: 'Build the song first: Live runs the effect on the sound that feeds this block.' };
    return { ok: true, reason: 'Hear the curve change while you drag a point (approximate: the effect runs in the browser).' };
  }

  function halt() {
    generation++;
    if (ticker) { clearInterval(ticker); ticker = 0; }
    if (mode) { stop(); setLoop(false); }
    mode = null;
  }

  /** Window of bars as the build range, or null for the whole song. */
  const windowOf = (centerBar, whole) => (whole ? null : previewWindow(centerBar, totalBars));
  const label = (win) => (win ? `bars ${win.firstBar} to ${win.firstBar + win.bars - 1}` : 'the whole song');

  async function startLive(centerBar, whole) {
    const state = liveState();
    if (!state.ok) { onStatus(state.reason); return false; }
    halt();
    const mine = ++generation;
    onStatus('Loading the sound that feeds this block...');
    try {
      const take = inputTake();
      const [blob, record] = await Promise.all([fetchTakeAudio(take), api.get(`/api/takes/${encodeURIComponent(take)}`)]);
      if (mine !== generation) return false;
      const firstBar = Number(record.plan?.settings?.firstBar) || 1;
      const node = flat().nodes.find((n) => n.id === block);
      const base = Object.fromEntries(Object.entries(node?.params ?? {}).map(([k, v]) => [k, unquote(v)]));
      await playLivePreview(blob, (context, source, output) => liveEffectGraph(context, source, output, type, base, tempo));
      if (mine !== generation) return false;
      mode = 'live';
      const win = windowOf(centerBar, whole), duration = playerState().duration || 0;
      if (win && duration > 0) {
        const start = Math.max(0, (win.firstBar - firstBar) * secondsPerBar(tempo)), end = Math.min(duration, start + win.bars * secondsPerBar(tempo));
        if (end > start) { setLoopRegion([start / duration, end / duration]); seekFraction(start / duration); }
      }
      ticker = setInterval(() => {
        const st = playerState();
        if (!st.playing) return;
        const value = automationValueAt(getPoints(), songBar(st.time, tempo, firstBar), range);
        if (value !== null) updateLivePreview({ ...base, [name]: String(Math.round(value * 100) / 100) });
      }, 30);
      onStatus(`Live · approximate · ${label(win)}`);
      return true;
    } catch (error) { if (mine === generation) { mode = null; onStatus(error.message || String(error)); } return false; }
  }

  async function startDraft(centerBar, whole) {
    halt();
    const mine = ++generation;
    const win = windowOf(centerBar, whole);
    onStatus(`Rendering ${label(win)} with this curve...`);
    try {
      const report = await api.post('/api/build', { path: S.path, intent: 'preview', ...(win ? { range: win } : {}), override: { block, name, value: getText() } });
      if (mine !== generation) return false;
      if (!report.masterTakeId) throw new Error('The song has no master to preview.');
      await play(report.masterTakeId);
      if (mine !== generation) return false;
      setLoop(Boolean(win));
      mode = 'draft';
      onStatus(`Preview · exact · ${label(win)}`);
      return true;
    } catch (error) { if (mine === generation) { mode = null; onStatus(error.message || String(error)); } return false; }
  }

  return {
    liveState,
    startLive,
    startDraft,
    stop: () => { halt(); onStatus(''); },
    mode: () => mode,
  };
}
