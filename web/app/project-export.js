import { api } from './api.js';
import { el, toast } from './ui.js';

const download = (url) => { const link = el('a', { href: url, download: '' }); document.body.append(link); link.click(); link.remove(); };
export function exportOriginal(item) { download(`/api/project/export?path=${encodeURIComponent(item.path)}`); }

/** Keep conversion/compression visible and cancellable without buffering the download in the browser. */
export async function exportAs(item, format) {
  const heading = el('h2', { textContent: `Export as ${format.toUpperCase()}` });
  const step = el('p', { textContent: 'Preparing export...', role: 'status', 'aria-live': 'polite' });
  const progress = el('progress', { max: 1, 'aria-label': 'Export progress' });
  const stats = el('p', { className: 'dim' });
  const cancel = el('button', { type: 'button', textContent: 'Cancel export' });
  const close = el('button', { type: 'button', className: 'tx-x', textContent: '✕', 'aria-label': 'Cancel export' });
  const body = el('div', { className: 'tx-body' }, [el('p', { textContent: item.path }), step, progress, stats]);
  const foot = el('div', { className: 'tx-foot' }, [cancel]);
  const box = el('dialog', { className: 'tx-dialog project-export-dialog', 'aria-label': `Export ${item.name}` }, [el('div', { className: 'tx-head' }, [heading, el('span', { className: 'sp' }), close]), body, foot]);
  document.body.append(box); box.showModal();
  let id = null, cancelled = false, terminal = false;
  const requestCancel = async () => {
    if (terminal) { box.close(); return; }
    cancelled = true; cancel.disabled = true; step.textContent = 'Cancelling export...';
    if (id) try { await api.post('/api/project/export/cancel', { id }); } catch (error) { toast(error.message); cancel.disabled = false; }
  };
  cancel.onclick = requestCancel; close.onclick = requestCancel;
  box.addEventListener('cancel', (event) => { if (!terminal) { event.preventDefault(); requestCancel(); } });
  box.addEventListener('close', () => box.remove());
  try {
    let job = await api.post('/api/project/export', { path: item.path, format });
    id = job.id;
    if (cancelled) await api.post('/api/project/export/cancel', { id });
    while (true) {
      step.textContent = cancelled && job.state === 'running' ? 'Cancelling export...' : job.step;
      if (job.fraction == null || job.step === 'Counting files') progress.removeAttribute('value'); else progress.value = job.fraction;
      stats.textContent = `${Math.floor(job.elapsedSeconds)}s${format === 'zip' ? ` · ${job.files}/${job.totalFiles} files` : ''} · ${(job.bytes / 1048576).toFixed(1)} MB${job.totalBytes ? ` / ${(job.totalBytes / 1048576).toFixed(1)} MB` : ''}${job.audioSeconds ? ` · ${job.audioSeconds.toFixed(1)}s audio converted` : ''}`;
      if (job.state !== 'running') {
        terminal = true; cancel.disabled = false; cancel.textContent = 'Close'; close.setAttribute('aria-label', 'Close');
        if (job.state === 'ready' && !cancelled) {
          progress.value = 1;
          const url = `/api/project/export/download?id=${encodeURIComponent(id)}`;
          foot.prepend(el('button', { type: 'button', className: 'primary', textContent: 'Download again', onclick: () => download(url) }));
          download(url);
        }
        break;
      }
      await new Promise((resolve) => setTimeout(resolve, 350));
      job = await api.get(`/api/project/export/status?id=${encodeURIComponent(id)}`);
    }
  } catch (error) {
    terminal = true; step.textContent = error.message || 'Export failed.'; cancel.disabled = false; cancel.textContent = 'Close'; close.setAttribute('aria-label', 'Close');
    if (id) api.post('/api/project/export/cancel', { id }).catch(() => {});
  }
}
