// The API client: token handling, JSON calls with problem details, and the event stream with reconnect.

const KEY = 'ample.token';
let token = '';
let authLost = false;

function reportAuthLost() {
  if (authLost) return;
  authLost = true;
  document.dispatchEvent(new Event('ample:session-lost'));
}

/** Reads the session token from the address fragment (once), keeps it for this tab and for the event stream cookie. */
export function initToken() {
  const m = /#token=([0-9a-f]+)/.exec(location.hash);
  try {
    if (m) {
      sessionStorage.setItem(KEY, m[1]);
      history.replaceState(null, '', location.pathname + location.search);
    }
    token = sessionStorage.getItem(KEY) || '';
  } catch {
    token = m ? m[1] : '';
  }
  document.cookie = 'ample-token=; path=/; max-age=0; SameSite=Strict';   // the name of an older version: it would only confuse which session a tab belongs to
  if (token) document.cookie = `ample-token-${location.port || (location.protocol === 'https:' ? '443' : '80')}-${token.slice(0, 16)}=${token}; path=/; max-age=604800; SameSite=Strict`;
  forgetOldTokenCookies(token ? `ample-token-${location.port || (location.protocol === 'https:' ? '443' : '80')}-${token.slice(0, 16)}` : '');
  if (!token) goToSignInIfAvailable();
  return token;
}

/**
 * Every sign-in makes a cookie with a new name (the name holds the start of the token), and none of them ever expired: after many sign-ins the Cookie header of a request
 * grew past what nginx reads over HTTP/1.1 (8 KB), and the language-server WebSocket was answered with a 400. Only the cookie of this session is kept.
 */
const KEEP_OTHER_TOKEN_COOKIES = 4;
function forgetOldTokenCookies(current) {
  try {
    // the cookies of other sessions may still be in use by another tab (the server takes any valid one), so the newest few stay and only the pile of old ones goes
    const others = document.cookie.split(';').map((pair) => pair.split('=')[0].trim()).filter((name) => name.startsWith('ample-token-') && name !== current);
    for (const name of others.slice(0, Math.max(0, others.length - KEEP_OTHER_TOKEN_COOKIES))) document.cookie = `${name}=; path=/; max-age=0; SameSite=Strict`;
  } catch { /* cookies may be blocked: nothing to clean then */ }
}

/** A tab without any token cannot work: when the server has a sign-in, go straight to it instead of waiting for the first failing call. */
function goToSignInIfAvailable() {
  if (location.pathname === '/login') return;
  fetch('/login', { cache: 'no-store' }).then(r => { if (r.ok) location.replace(location.pathname.startsWith('/remix') ? '/login?next=/remix/' : '/login'); }).catch(() => { /* no sign-in: the session dialog explains it */ });
}

/** The session token of this tab. */
export const getToken = () => token;

export class ApiError extends Error {
  constructor(problem) {
    super(problem.detail || problem.title || 'The request failed');
    this.code = problem.code || '';
    this.status = problem.status || 0;
  }
}

async function call(method, path, body, signal) {
  const headers = { 'X-Ample-Token': token };
  if (body !== undefined) headers['Content-Type'] = 'application/json';
  const requestSignal = method === 'GET' ? (signal ? AbortSignal.any([signal, AbortSignal.timeout(15000)]) : AbortSignal.timeout(15000)) : signal;
  const r = await fetch(path, { method, headers, body: body === undefined ? undefined : JSON.stringify(body), signal: requestSignal });
  if (!r.ok) {
    let p = {};
    try { p = await r.json(); } catch { /* not JSON */ }
    if (r.status === 401 && !p.detail) p.detail = 'This tab no longer matches the server (it was restarted or the token changed). Open the address that "ample open" gave, or reload with the link of the running server.';
    if (r.status === 401 && p.code !== 'AMP5458') reportAuthLost();
    throw new ApiError({ ...p, status: r.status });
  }
  return r.status === 204 ? null : r.json();
}

const inflight = new Map();

export const api = {
  // the same GET asked while it is still on its way shares that one request (startup asks for the same tree and the same take more than once); nothing is kept after it answers
  get: (p, signal) => {
    if (signal) return call('GET', p, undefined, signal);
    let pending = inflight.get(p);
    if (!pending) { pending = call('GET', p).finally(() => inflight.delete(p)); inflight.set(p, pending); }
    return pending;
  },
  put: (p, b) => call('PUT', p, b),
  patch: (p, b) => call('PATCH', p, b),
  delete: (p) => call('DELETE', p),
  post: (p, b, signal) => call('POST', p, b ?? {}, signal),
  effectPreview: async (body) => {
    const r = await fetch('/api/effects/preview', { method: 'POST', headers: { 'X-Ample-Token': token, 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
    if (!r.ok) { let problem = {}; try { problem = await r.json(); } catch { /* not JSON */ } throw new ApiError({ ...problem, status: r.status }); }
    return r.blob();
  },
  effectImpulse: async (body) => {
    const r = await fetch('/api/effects/impulse', { method: 'POST', headers: { 'X-Ample-Token': token, 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
    if (!r.ok) { let problem = {}; try { problem = await r.json(); } catch { /* not JSON */ } throw new ApiError({ ...problem, status: r.status }); }
    return r.blob();
  },
  /** Sends bytes (a WAV) and reads the JSON that comes back. */
  postBinary: async (path, bytes) => {
    const r = await fetch(path, { method: 'POST', headers: { 'X-Ample-Token': token, 'Content-Type': 'application/octet-stream' }, body: bytes });
    if (!r.ok) { let problem = {}; try { problem = await r.json(); } catch { /* not JSON */ } throw new ApiError({ ...problem, status: r.status }); }
    return r.json();
  },
  gmAudition: async (body) => {
    const r = await fetch('/api/gm/audition', { method: 'POST', headers: { 'X-Ample-Token': token, 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
    if (!r.ok) { let problem = {}; try { problem = await r.json(); } catch { /* not JSON */ } throw new ApiError({ ...problem, status: r.status }); }
    return { blob: await r.blob(), warnings: r.headers.get('X-Ample-GM-Warnings') || '' };
  },
  upload: async (p, file, extension, track) => {
    const r = await fetch(`${p}?extension=${encodeURIComponent(extension)}&track=${encodeURIComponent(track)}`, { method: 'POST', headers: { 'X-Ample-Token': token, 'Content-Type': 'application/octet-stream' }, body: file });
    if (!r.ok) { let problem = {}; try { problem = await r.json(); } catch { /* not JSON */ } throw new ApiError({ ...problem, status: r.status }); }
    return r.json();
  },
  uploadProject: (file, dir, onConflict, onProgress) => new Promise((resolve, reject) => {
    const query = new URLSearchParams({ name: file.name, dir, onConflict });
    const request = new XMLHttpRequest();
    request.open('POST', `/api/project/upload?${query}`);
    request.setRequestHeader('X-Ample-Token', token);
    request.setRequestHeader('Content-Type', 'application/octet-stream');
    request.responseType = 'json';
    request.upload.onprogress = (event) => { if (event.lengthComputable) onProgress(file, event.loaded, event.total); };
    request.onload = () => {
      const problem = request.response && typeof request.response === 'object' ? request.response : {};
      if (request.status === 409) { resolve({ conflict: true, ...problem }); return; }
      if (request.status < 200 || request.status >= 300) { reject(new ApiError({ ...problem, status: request.status })); return; }
      resolve(problem);
    };
    request.onerror = () => reject(new ApiError({ detail: `Upload failed for ${file.name}`, status: request.status }));
    request.onabort = () => reject(new ApiError({ detail: `Upload cancelled for ${file.name}`, status: request.status }));
    request.send(file);
  }),
  createProjectFolder: (path) => call('POST', '/api/project/folder', { path }),
  moveProjectPath: (source, destination) => call('POST', '/api/project/rename', { source, destination }),
  deleteBundle: (id) => call('DELETE', `/api/bundles/${encodeURIComponent(id)}`),
  deleteProjectFile: (path) => call('DELETE', `/api/project/file?path=${encodeURIComponent(path)}`),
  deleteProjectFolder: (path) => call('DELETE', `/api/project/folder?path=${encodeURIComponent(path)}`),
  projectFileHash: (path) => call('GET', `/api/project/hash?path=${encodeURIComponent(path)}`),
  projectFilePeaks: (path) => call('GET', `/api/project/peaks?path=${encodeURIComponent(path)}`),
  projectFilePreview: (path) => call('GET', `/api/project/preview?path=${encodeURIComponent(path)}`),
  transcribeAudio: async (file, plugin, options, job, allowGpu, signal) => {
    const body = new FormData(); body.set('audio', file, file.name); body.set('plugin', plugin); body.set('options', JSON.stringify(options)); body.set('job', job); body.set('allowGpu', String(allowGpu));
    const r = await fetch('/api/audio/transcriptions', { method: 'POST', headers: { 'X-Ample-Token': token }, body, signal });
    if (!r.ok) { let problem = {}; try { problem = await r.json(); } catch { /* not JSON */ } throw new ApiError({ ...problem, status: r.status }); }
    return r.json();
  },
  transcriptionPeaks: async (id, role) => {
    const r = await fetch(`/api/audio/transcriptions/${encodeURIComponent(id)}/peaks?role=${encodeURIComponent(role)}&points=800`, { headers: { 'X-Ample-Token': token } });
    if (!r.ok) { let problem = {}; try { problem = await r.json(); } catch { /* not JSON */ } throw new ApiError({ ...problem, status: r.status }); }
    return r.json();
  },
  /** Renders the audio of a take as WAV or MP3 and hands it to the browser as a download (nothing is written in the project). */
  downloadTake: async (take, name, format) => {
    const r = await fetch(`/api/takes/${encodeURIComponent(take)}/export`, { method: 'POST', headers: { 'X-Ample-Token': token, 'Content-Type': 'application/json' }, body: JSON.stringify({ name, format, download: true }) });
    if (!r.ok) { let problem = {}; try { problem = await r.json(); } catch { /* not JSON */ } throw new ApiError({ ...problem, status: r.status }); }
    const blob = await r.blob(), url = URL.createObjectURL(blob), anchor = document.createElement('a');
    anchor.href = url; anchor.download = r.headers.get('Content-Disposition')?.match(/filename="?([^";]+)"?/)?.[1] || `${name}.${format}`; document.body.append(anchor); anchor.click(); anchor.remove(); setTimeout(() => URL.revokeObjectURL(url), 0);
    return blob.size;
  },
  downloadMidi: async (file) => {
    const r = await fetch(`/api/midi/download?file=${encodeURIComponent(file)}`, { headers: { 'X-Ample-Token': token } });
    if (!r.ok) { let problem = {}; try { problem = await r.json(); } catch { /* not JSON */ } throw new ApiError({ ...problem, status: r.status }); }
    const blob = await r.blob(), url = URL.createObjectURL(blob), anchor = document.createElement('a');
    anchor.href = url; anchor.download = r.headers.get('Content-Disposition')?.match(/filename="?([^";]+)"?/)?.[1] || 'roundtrip.mid'; document.body.append(anchor); anchor.click(); anchor.remove(); setTimeout(() => URL.revokeObjectURL(url), 0);
  },
  activateMidiModule: (body) => call('POST', '/api/midi/module-activate', body),
  downloadMidiModule: async (name) => {
    const r = await fetch('/api/midi/module-download', { method: 'POST', headers: { 'X-Ample-Token': token, 'Content-Type': 'application/json' }, body: JSON.stringify({ name }) });
    if (!r.ok) { let problem = {}; try { problem = await r.json(); } catch { /* not JSON */ } throw new ApiError({ ...problem, status: r.status }); }
    const warnings = r.headers.get('X-Ample-Midi-Warnings') || '', blob = await r.blob(), url = URL.createObjectURL(blob), anchor = document.createElement('a');
    anchor.href = url; anchor.download = r.headers.get('Content-Disposition')?.match(/filename="?([^";]+)"?/)?.[1] || `${name}-edited.mid`; document.body.append(anchor); anchor.click(); anchor.remove(); setTimeout(() => URL.revokeObjectURL(url), 0);
    return warnings;
  },
};

/** Opens the event stream; reconnects with a growing pause. `handlers` maps event names to functions (plus `open`). */
export function connectEvents(handlers, { hideGraceMs = 3 * 60 * 1000 } = {}) {
  let es = null;
  let delay = 500;
  let stopped = false;
  let reconnect = 0;
  const open = () => {
    if (stopped || authLost || document.hidden) return;
    es = new EventSource('/api/events');
    for (const [name, fn] of Object.entries(handlers)) {
      if (name !== 'open') es.addEventListener(name, (e) => fn(JSON.parse(e.data)));
    }
    es.onopen = () => { delay = 500; if (handlers.open) handlers.open(); };
    es.onerror = () => {
      es.close();
      clearTimeout(reconnect);
      if (stopped || authLost) return;
      call('GET', '/api/health').then(scheduleReconnect, scheduleReconnect);
    };
  };
  const scheduleReconnect = () => {
    if (!stopped && !authLost) reconnect = setTimeout(open, (delay = Math.min(delay * 2, 8000)));
  };
  open();
  // a tab that is hidden keeps its stream for a few minutes: a short look at another tab costs nothing and nothing is loaded again on return
  let closeTimer = 0;
  const release = () => { clearTimeout(reconnect); es?.close(); es = null; };
  const visibility = () => {
    clearTimeout(closeTimer);
    if (document.hidden) { if (hideGraceMs > 0) closeTimer = setTimeout(release, hideGraceMs); else release(); return; }
    if (es && es.readyState !== 2) return;   // still open: nothing to do
    release(); open();
  };
  document.addEventListener('visibilitychange', visibility);
  return () => { stopped = true; clearTimeout(reconnect); clearTimeout(closeTimer); document.removeEventListener('visibilitychange', visibility); if (es) es.close(); };
}
