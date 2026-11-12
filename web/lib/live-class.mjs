// The live class of every block template and, for a block that does not play live, the one-line reason (local-preview chunk 7.1).
// Classes: live (plays live, flagged livePreview in its template), offline (a browser render exists, live comes with a later chunk),
// buffer (an operation on a decoded take), source (plays a take it already has), midi (MIDI transform, heard through an instrument),
// sampled (needs a SoundFont or sample render), server (generation or analysis only the server does), data (shapes other values, no audio), visual (display only).
// Kept here, not in spec/templates: it describes the browser, not the language.

const live = ['volume', 'eq', 'peq', 'filter', 'compressor', 'duck', 'distortion', 'delay', 'reverb', 'chorus', 'convolution', 'tremolo', 'flanger', 'phaser', 'pan', 'width', 'gate', 'limiter', 'deesser'];
const T = {};
for (const id of live) T[id] = ['live', ''];
const add = (cls, reason, ids) => { for (const id of ids.split(' ')) T[id] = [cls, reason]; };

add('offline', 'Previews from a browser render when you stop moving; a live version is planned.', 'bitcrusher pitch stretch granular denoise autotune chop vocoder');
T['delay-llama'] = ['live', ''];
add('offline', 'Plays from a render of the built sources; live moves are planned.', 'meter normalize');
add('buffer', 'An edit of the decoded take; heard after a short re-render, live moves are planned.', 'cut fade shift join crossfade reverse splitter trim silence rewind scratch');
add('source', 'A source: it plays the take it makes, there is nothing to move live.', 'import-audio ambience click');
add('offline', 'A voice that plays from a browser render; live notes are planned.', 'synth bass piano sampler drums guitar sequencer');
add('midi', 'Changes notes; you hear it through the instrument after it.', 'transpose chord quantize midi-humanize arpeggiator midi-clip');
add('sampled', 'Needs the SoundFont render of the server; the browser plays the built take.', 'gm midi-render midi');
add('server', 'Made by a model on the server; the browser plays the built take, the generation itself is never live.', 'ace-music ai-voice voice-clone choir');
add('visual', 'Display only: it analyses or selects, it makes no sound of its own.', 'scorer variants mood-scene lyrics');
add('data', 'Shapes other values over time; it carries no audio.', 'curve fire follow map');
add('buffer', 'Joins the results of regions; region ports are not audio ports.', 'region-merge');
add('offline', 'Mix and routing: plays through the built mix; the live mixer moves levels and pans.', 'mixer bus send master group');

/** { id: [class, reason] } for every template the browser knows. */
export const LIVE_CLASS = T;

/** The class of a template id ('unknown' for a template this table does not list). */
export const liveClassOf = (id) => (T[id] || ['unknown', ''])[0];

/** The one-line reason a block does not play live, or '' when it does. */
export const whyNotLive = (id) => (T[id] || ['unknown', ''])[1];

// Blocks that play live but whose window has its own preview path (the parametric EQ draws and plays itself, Delay Llama has its own live engine), and blocks without a live graph
// that the Advanced window still previews from a browser render of the input (denoise, and the master with its limiter and meter).
const OWN_PREVIEW = new Set(['peq', 'delay-llama']), RENDERED_LOCALLY = new Set(['denoise', 'master']);
/** Whether the Advanced window previews this block in the browser on the take that feeds it (replaces the hard-coded set of the window, local-preview chunk 8.4). */
export const hasLocalPreview = (id) => (liveClassOf(id) === 'live' && !OWN_PREVIEW.has(id)) || RENDERED_LOCALLY.has(id);
