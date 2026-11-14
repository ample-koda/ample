// Waits that end when they are cancelled. Pure (no DOM globals, no store), so they are tested in node.
// A cancelled build kept waiting for the answer of a request it had started, and a cancelled audio preview kept waiting for an event
// that a stopped player never sends, so the transport stayed busy until the answer came or a timeout ended it.

/** An error with the name that fetch and the build code use for a cancelled operation. */
export function abortError(message = 'The operation was cancelled.') {
  const error = new Error(message);
  error.name = 'AbortError';
  return error;
}

/**
 * The result of `promise`, or an AbortError as soon as `signal` is aborted. A late answer of the original promise is ignored
 * (and its failure does not become an unhandled rejection).
 */
export function abortable(promise, signal) {
  if (!signal) return Promise.resolve(promise);
  if (signal.aborted) {
    Promise.resolve(promise).catch(() => {});
    return Promise.reject(abortError());
  }
  return new Promise((resolve, reject) => {
    const onAbort = () => reject(abortError());
    signal.addEventListener('abort', onAbort, { once: true });
    Promise.resolve(promise).then(
      (value) => { signal.removeEventListener('abort', onAbort); resolve(value); },
      (error) => { signal.removeEventListener('abort', onAbort); reject(error); },
    );
  });
}

/**
 * Waits for the first of: an event named in `ok` (resolves true), an event named in `fail` (rejects with makeError()),
 * or cancel() (resolves false). Every listener is removed when it ends, whichever way.
 * @returns {{ promise: Promise<boolean>, cancel: () => void }}
 */
export function eventOrCancel(target, ok, fail, makeError = () => new Error('The media could not be loaded.')) {
  let settle;
  const promise = new Promise((resolve, reject) => {
    const handlers = [];
    const done = (finish) => () => {
      for (const [name, fn] of handlers) target.removeEventListener(name, fn);
      finish();
    };
    const succeed = done(() => resolve(true));
    const failed = done(() => reject(makeError()));
    for (const name of ok) { handlers.push([name, succeed]); target.addEventListener(name, succeed); }
    for (const name of fail) { handlers.push([name, failed]); target.addEventListener(name, failed); }
    settle = done(() => resolve(false));
  });
  return { promise, cancel: () => settle() };
}
