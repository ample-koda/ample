// What the pill of a background Assistant task does with the answer of one status request. Pure, so node --test checks it.
// A request that fails (the network, a server that is restarting) says nothing about the task: the pill stays and says it is reconnecting.
// Only an answer that says the task is gone (404) or that it is over takes the pill away.

const OVER = ['rejected', 'cancelled'];

/**
 * @param {{ error?: { status?: number } | null, task?: { status?: string, applied?: boolean } | null }} poll one answer
 * @returns {'reconnecting' | 'gone' | 'over' | 'show'} reconnecting: keep the pill and try again; gone: the task does not exist (stop tracking);
 * over: remove the pill, and stop tracking when the task was applied or rejected; show: draw the state of the task
 */
export function pillAction({ error = null, task = null } = {}) {
  if (error) return error.status === 404 || error.status === 410 ? 'gone' : 'reconnecting';
  if (!task) return 'gone';
  if (task.applied || OVER.includes(task.status)) return 'over';
  return 'show';
}

/** Whether the tracking of the task ends: the task is gone, was applied or was rejected (a cancelled one only loses its pill). */
export const endsTracking = (action, task) => action === 'gone' || (action === 'over' && Boolean(task?.applied || task?.status === 'rejected'));
