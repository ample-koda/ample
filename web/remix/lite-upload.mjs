// A POST that tells how much of the body has gone: fetch cannot do that, XMLHttpRequest can. The answer comes back as a real Response, with a body that streams while the server is
// still writing it (the build answers line by line), so the code that reads a fetch answer does not change.

/**
 * @param {string} url
 * @param {{headers?: Record<string, string>, body: FormData, signal?: AbortSignal, onProgress?: (sent: number, total: number) => void}} options
 * @returns {Promise<Response>} resolves when the answer starts (its status and headers are there)
 */
export function postWithProgress(url, { headers = {}, body, signal, onProgress = () => {} }) {
  return new Promise((resolve, reject) => {
    const xhr = new XMLHttpRequest();
    xhr.open('POST', url);
    for (const [name, value] of Object.entries(headers)) xhr.setRequestHeader(name, value);
    const encoder = new TextEncoder();
    let delivered = 0;
    let controller = null;
    let answered = false;
    const stream = new ReadableStream({
      start(c) { controller = c; },
      cancel() { xhr.abort(); },
    });
    const flush = () => {
      const text = xhr.responseText;
      if (text.length > delivered && controller) { controller.enqueue(encoder.encode(text.slice(delivered))); delivered = text.length; }
    };
    xhr.upload.onprogress = (event) => { if (event.lengthComputable) onProgress(event.loaded, event.total); };
    xhr.onreadystatechange = () => {
      if (xhr.readyState === 2 && !answered) {
        answered = true;
        const parsed = new Headers();
        for (const line of xhr.getAllResponseHeaders().trim().split(/[\r\n]+/)) {
          const at = line.indexOf(':');
          if (at > 0) parsed.append(line.slice(0, at).trim(), line.slice(at + 1).trim());
        }
        resolve(new Response(stream, { status: xhr.status, headers: parsed }));
      }
    };
    xhr.onprogress = flush;
    xhr.onload = () => { flush(); controller?.close(); };
    xhr.onerror = () => { const error = new TypeError('The connection to the server failed.'); if (answered) controller?.error(error); else reject(error); };
    xhr.onabort = () => { const error = new DOMException('The request was cancelled.', 'AbortError'); if (answered) controller?.error(error); else reject(error); };
    if (signal) {
      if (signal.aborted) { reject(new DOMException('The request was cancelled.', 'AbortError')); return; }
      signal.addEventListener('abort', () => xhr.abort(), { once: true });
    }
    xhr.send(body);
  });
}
