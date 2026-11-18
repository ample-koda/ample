// Live Preview: Ctrl+Enter builds the target and keeps the build for Play (Listen). Whether it may be used is decided here, from what the build was made of and what
// is true now: the target and the quality (the key of the job), the text of the song, its version, the files the song reads (the hash of /api/doc/inputs) and whether
// the song reads the clock (a dynamic song is built again: its values move). @ample:listen-preview

/** What a build was made of, taken when it started (the inputs and the version) and when it ended (the text it built). */
export const preparedOf = ({ result, version, inputs, dynamic }) => ({ result, key: result.job.key, text: result.text, version, inputs, dynamic: !!dynamic });

/**
 * Whether a prepared build may play instead of a new build.
 * @param {ReturnType<typeof preparedOf>|null} prepared
 * @param {{ key: string, text: string, version: number, inputs: string|null, dynamic: boolean }} now the job, the text, the version of the song,
 *   the hash of its inputs, and whether it is dynamic, as they are now
 */
export function preparedUsable(prepared, now) {
  if (!prepared || !prepared.result) return false;
  if (prepared.dynamic || now.dynamic) return false;
  if (prepared.key !== now.key) return false;
  if (prepared.text !== now.text || prepared.version !== now.version) return false;
  if (!prepared.inputs || !now.inputs || prepared.inputs !== now.inputs) return false;
  return true;
}
