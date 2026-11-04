// The pure part of the engine parameter of the ACE blocks (plan engine-per-block): reading and writing "route:model" and what would stop the build. @ample:engine
const CAPABILITY = { 'ace-music': 'song', 'ai-voice': 'voice-ace', 'voice-clone': 'voice-clone' };

/** Splits the text of an engine parameter into its place and its model (both empty for an empty text). */
export function parseEngine(text) {
  const value = String(text ?? '').trim().replace(/^"|"$/g, '').trim();
  const colon = value.indexOf(':');
  return colon < 0 ? { route: value, model: '' } : { route: value.slice(0, colon).trim(), model: value.slice(colon + 1).trim() };
}

/** The text to write for a place and a model: empty for the project default, else "route" or "route:model" (the quotes are written by the caller). */
export const formatEngine = (route, model) => (route ? (model ? `${route}:${model}` : route) : '');

/** What would stop the build for this choice, from what the servers say; an empty text when nothing is known to be wrong. */
export function engineProblem({ blockType, route, model, state }) {
  if (!route) return '';
  const faerWanted = route === 'faer';
  const connected = Boolean(state.faer?.configured && state.faer?.connected);
  if (faerWanted && !state.faer?.configured) return 'This machine has no Faer connection: the build stops with AMP5380 (an explicit Faer is never replaced by local).';
  if (faerWanted && !connected) return 'Faer is not reachable now: the build stops until it is.';
  const capability = state.faer?.status?.capabilities?.[CAPABILITY[blockType]];
  if (faerWanted && connected && capability && !capability.ready) return 'Faer is connected but this capability is not ready there.';
  if (model.endsWith('.gguf')) {
    const entry = state.models.find((m) => m.file === model);
    if (route === 'local' && entry && !entry.local) return 'This model file is not on the local ACE server (AMP5382).';
    if (faerWanted && entry && !entry.faer) return 'This model file is not on the Faer server.';
    if (!entry && state.models.length) return 'Neither server lists this model file.';
  }
  return '';
}
