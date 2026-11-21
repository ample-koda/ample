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

/** The defaults to show for an engine; the written parameters win over them. */
export function speechShown(params, engine) {
  return { ...SPEECH_DEFAULTS, ...(engine === 'flite' ? { pitch_hz: FLITE_PITCH_DEFAULT } : {}), ...params };
}
