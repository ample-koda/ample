// Koda's own login: a window that says whether Koda works in a lane of their own (so they can work beside the assistant) or shares the assistant's, and links or unlinks it with the Codex device flow. @ample:koda
import { api } from './api.js';
import { el, toast } from './ui.js';
import { openWindow } from './window.js';

export async function openKodaLogin() {
  const status = el('p', { className: 'kl-status', role: 'status' });
  const code = el('div', { className: 'kl-code', hidden: true });
  const link = el('button', { type: 'button', className: 'primary', textContent: 'Link a login for Koda' });
  const cancel = el('button', { type: 'button', textContent: 'Cancel', hidden: true });
  const unlink = el('button', { type: 'button', textContent: 'Unlink', hidden: true });
  let timer = 0;
  const paint = (s) => {
    const waiting = s.login?.state === 'waiting';
    status.textContent = s.linked
      ? 'Koda has a login of their own. They can work at the same time as the assistant.'
      : waiting ? 'Waiting for you to enter the code.' : s.login?.state === 'failed' ? `The link failed: ${s.login.error || 'unknown reason'}.` : 'Koda shares the lane of the assistant: they wait while the assistant works. A second login of the same ChatGPT account lets them work side by side.';
    code.hidden = !waiting;
    code.replaceChildren(...(waiting ? [el('p', { textContent: 'Open this address and enter the code:' }), el('a', { href: s.login.verificationUrl, target: '_blank', rel: 'noopener', textContent: s.login.verificationUrl }), el('b', { className: 'kl-user', textContent: s.login.userCode })] : []));
    link.hidden = s.linked || waiting;
    cancel.hidden = !waiting;
    unlink.hidden = !s.linked;
    if (waiting && !timer) timer = setInterval(refresh, 2000);
    if (!waiting && timer) { clearInterval(timer); timer = 0; }
  };
  async function refresh() { try { paint(await api.get('/api/koda/status')); } catch (e) { status.textContent = e.message || String(e); } }
  const run = (path) => async () => { try { paint(await api.post(path, {})); } catch (e) { toast(e.message || String(e)); } };
  link.onclick = run('/api/koda/login/start');
  cancel.onclick = run('/api/koda/login/cancel');
  unlink.onclick = run('/api/koda/unlink');
  const win = openWindow({
    id: 'koda-login', title: "Koda's login", width: 460, height: 320,
    content: el('div', { className: 'kl' }, [status, code, el('div', { className: 'kl-buttons' }, [link, cancel, unlink])]),
    buttons: [{ label: 'Close', value: 'close', primary: true }],
    onClose: () => { if (timer) clearInterval(timer); },
  });
  await refresh();
  return win;
}
