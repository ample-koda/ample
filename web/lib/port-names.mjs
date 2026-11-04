/**
 * The name a song writes for a port: the stream it carries (audio, notes, render, audio1, audio2 ...). It mirrors src/Ample.Language/PortNames.cs.
 * A port is a [name, type] pair as the graph model has them; `siblings` are the ports of the same side of the block.
 */
export function streamName(port, siblings, output) {
  const [name, type] = port;
  const taken = (other) => siblings.some((p) => p !== port && p[0] === other);
  if (!output && type === 'render' && name === 'render_in') return taken('render') ? name : 'render';
  if (type !== 'audio') return name;
  if (output) return ['out', 'mix', 'mixed', 'master'].includes(name) && !taken('audio') ? 'audio' : name;
  if (name === 'in' || name === 'mix') return taken('audio') ? name : 'audio';
  const lane = /^in(\d+)$/.exec(name);
  return lane ? `audio${lane[1]}` : name;
}

/** What a port says when the pointer is on it: its stream name and what it connects to. */
export function portHint(port, siblings, output) {
  const [name, type] = port;
  const stream = streamName(port, siblings, output);
  const kind = type === 'midi' || type === 'midi-sequence' ? 'Events (MIDI)' : type === 'audio' ? 'Audio, rendered sound' : type === 'render' ? 'Render recipe: source and ordered effects' : type;
  const alias = stream === name ? '' : ` (older name: ${name})`;
  return `${stream}${alias} · ${kind}. ${output ? `Connects to a ${type} input.` : `Takes a ${type} stream.`}`;
}
