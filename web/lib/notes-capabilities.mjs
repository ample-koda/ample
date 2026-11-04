// What the piano roll can do with each part of a clip's notes (plan G04). The roll shows and edits what it can; everything else stays in the text, byte for byte,
// and is marked in the roll, and "Edit as text" always holds everything. This table is the one place that says which is which; a test checks that it names every
// field the notes model reads, so a new field cannot slip in unmarked. @ample:notes-model

/** show: drawn in the roll; edit: changed by gestures; where: how a user reaches what the roll does not do. */
export const NOTE_FIELDS = {
  bar: { show: true, edit: true, how: 'drag the note sideways over the bar lines' },
  start: { show: true, edit: true, how: 'drag the note, or the arrow keys' },
  length: { show: true, edit: true, how: 'drag the right edge' },
  pitch: { show: true, edit: true, how: 'drag the note up or down, or Shift+arrows for an octave' },
  velocity: { show: true, edit: true, how: 'drag the bar in the velocity lane' },
  channel: { show: true, edit: false, how: 'kept as written; change it in the text' },
  bank: { show: true, edit: false, how: 'kept as written; change it in the text' },
  program: { show: true, edit: false, how: 'kept as written; change it in the text' },
  tie: { show: true, edit: true, how: 'bar-split pieces appear as one note; resize its right edge or set its length in note properties' },
  pan: { show: true, edit: true, how: 'the lower lane (pan), a centred stem; PR06 extension key, no engine sound yet' },
  fine: { show: true, edit: true, how: 'the lower lane (fine pitch), a centred stem; PR06 extension key, no engine sound yet' },
  rel: { show: true, edit: true, how: 'the lower lane (release); PR06 extension key, no engine sound yet' },
  cut: { show: true, edit: true, how: 'the lower lane (cutoff); PR06 extension key, no engine sound yet' },
  slide: { show: true, edit: true, how: 'a dashed connector from the previous note; toggled with S or the note-properties popover; the engine glide is not built yet (PR08)' },
  port: { show: false, edit: false, how: 'kept as written; change it in the text (no roll UI yet)' },
  grp: { show: true, edit: true, how: 'colour group 1-8 (PR18): set in the note-properties popover, filtered with the Group selector' },
  extra: { show: false, edit: false, how: 'unknown `;key=value` pairs after a note; kept verbatim in the text, never shown, never edited in the roll' },
};

/** Parts of a clip that are not compact notes at all. */
export const CLIP_FORMS = {
  notes: { roll: 'edit', how: 'the compact form bar:start:length:pitch:velocity[:channel:bank:program[:tie]]' },
  'expanded notes': { roll: 'code', how: 'notes { note { ... } } is edited in the text; the roll lists it as not shown' },
  chords: { roll: 'convert', how: 'written out as notes the first time the roll is opened (one undoable edit)' },
  melody: { roll: 'convert', how: 'written out as notes the first time the roll is opened (one undoable edit)' },
  'midi file': { roll: 'view', how: 'a midi block is shown read-only; "Make an editable copy as notes" adds a midi-clip' },
  unknown: { roll: 'keep', how: 'an entry the roll cannot read stays in the text and is listed above the grid' },
};

/** A readable table (one line per field) for the docs and the help. */
export const describeCapabilities = () => [
  ...Object.entries(NOTE_FIELDS).map(([name, c]) => `${name}: ${c.show ? 'shown' : 'not shown'}, ${c.edit ? 'edited in the roll' : 'edited in the text'} (${c.how})`),
  ...Object.entries(CLIP_FORMS).map(([name, c]) => `${name}: ${c.roll} (${c.how})`),
];
