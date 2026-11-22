// What the speech block does when a parameter is not written, as the speech panel shows it on its knobs. The knobs start from the written
// value; without this table an unwritten parameter would show the bottom of the knob (rate 0.1, gain -60 dB) instead of what the block really does.
// The values are those of spec/templates/speech.json (a node test compares them); the flite voice sits lower and has its own pitch.

/** The default of each knob of the speech panel, as the text the source would hold. */
export const SPEECH_DEFAULTS = {
  rate: '1', gain: '0 dB', pitch_hz: '105 Hz', pitch_range: '11 Hz', fit_max_rate: '2', reference_midi: '60', reference_hz: '105 Hz',
  pause_scale: '1', brightness: '0 dB',
};

/** The loudness choice of a block that does not write a level (a node test compares it with the template). */
export const LEVEL_DEFAULT = 'matched';

/** The pitch of the flite voice when the block does not write one. */
export const FLITE_PITCH_DEFAULT = '95 Hz';

/** The clustergen voices of the flite engine: they speak text only (no phonemes, no words mode). */
export const FLITE_CG_VOICES = ['slt', 'rms', 'awb'];

/** The pitch and the pitch range of each voice of the flite engine when the block does not write them: the mean and the standard deviation of the F0 of its recording
 * (a C# test compares these numbers with the data of the voices). */
export const FLITE_VOICE_DEFAULTS = {
  kal16: { pitch_hz: '95 Hz', pitch_range: '11 Hz' },
  slt: { pitch_hz: '172 Hz', pitch_range: '27 Hz' },
  rms: { pitch_hz: '98 Hz', pitch_range: '24 Hz' },
  awb: { pitch_hz: '132 Hz', pitch_range: '25 Hz' },
};

/** The pitch range in Hz that words mode sings with each voice of the flite engine (measured, plan/speech/flite/x1a-pitch-range.md and cg-words.md; a C# test compares these numbers
 * with the engine): a note outside it is sung the same note an octave or more away. */
export const FLITE_WORDS_RANGE = { kal16: [50, 480], slt: [74, 650], rms: [55, 450], awb: [114, 486] };

/** The defaults to show for an engine (and, for flite, its voice); the written parameters win over them. */
export function speechShown(params, engine, voice = 'kal16') {
  const own = engine === 'flite' ? FLITE_VOICE_DEFAULTS[voice] ?? FLITE_VOICE_DEFAULTS.kal16 : {};
  return { ...SPEECH_DEFAULTS, ...own, ...params };
}
