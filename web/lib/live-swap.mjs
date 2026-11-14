// The Sequencer's Live update: build a change in the background and let the player bring it in. Live can be switched off at any moment, so it is
// checked before the build, after it and again just before the take is swapped in; a change that is still waiting never takes over after that.

/**
 * @param {{ state: { live: boolean, liveBusy?: boolean, liveAgain?: boolean, error?: unknown, wroteLive?: boolean, builtTake?: unknown, stale?: boolean },
 *   isPlaying: () => boolean, write: () => Promise<void>, build: () => Promise<void>, takeOf: () => unknown, swap: (take: unknown) => Promise<void>, setStatus: (text: string) => void }} deps
 */
export function createLiveSwap({ state: Q, isPlaying, write, build, takeOf, swap, setStatus }) {
  return async function liveSwap() {
    if (Q.liveBusy) { Q.liveAgain = true; return; }
    Q.liveBusy = true;
    try {
      do {
        Q.liveAgain = false;
        if (!Q.live || !isPlaying() || Q.error) return;
        setStatus('building the change...');
        await write();
        Q.wroteLive = true;
        await build();
        if (!Q.live) { setStatus(''); return; }
        const take = takeOf();
        if (!take) { setStatus('the change could not be built'); return; }
        Q.builtTake = take; Q.stale = false;
        if (Q.liveAgain) continue;
        if (!Q.live) { setStatus(''); return; }
        await swap(take);
        setStatus('the change plays when the loop comes round');
      } while (Q.liveAgain);
    } finally { Q.liveBusy = false; }
  };
}
