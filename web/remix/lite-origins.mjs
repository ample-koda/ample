// Remix: where the notes of the chain clips of the song that plays come from (note origin, plan/live-preview/note-origin.md). The server works it out over the text
// and the files of the build (/api/remix/notes/origins, the same evaluator as Studio's clip-notes route); the page only asks when the song has a chain at all.
import { chainClips } from '../lib/listen-users.mjs';
import { blockNames } from '../lib/listen-highlight.mjs';

/**
 * The origins of the chain clips of a song: { clip: [{ at, len, pitch, source, index, lap }] }, or {} when there is no chain or the server cannot say
 * (the marks then stay what they were without origins: the line of a chain). Never throws.
 * @param {{ text: string, files: { path: string, text?: string }[], post: (path: string, body: object) => Promise<{ origins?: object }> }} input
 */
export async function fetchOrigins({ text, files, post }) {
  try {
    const blocks = chainClips(text, blockNames(text, 'midi-clip'));
    if (!blocks.length) return {};
    const answer = await post('/api/remix/notes/origins', { files: files.filter((f) => f.text !== undefined).map(({ path, text: body }) => ({ path, text: body })), text, blocks });
    return answer && typeof answer.origins === 'object' && answer.origins ? answer.origins : {};
  } catch { return {}; }
}
