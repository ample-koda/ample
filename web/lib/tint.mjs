// The tint of a block (the tint parameter, any block type): only a colour of its card, never part of the sound. The names are the ones the language accepts (AMP2022). @ample:tint
export const TINTS = { red: '#e5484d', orange: '#f08a24', yellow: '#e2c744', green: '#46c28e', teal: '#2bb5b0', blue: '#4c9bff', purple: '#a07cf0', pink: '#ec6ab2' };

/** The tint name of a block's parameters ('' when there is none or it is not a known colour). */
export const tintOf = (params) => {
  const name = String(params?.tint ?? '').trim().replace(/^"|"$/g, '').trim();
  return Object.hasOwn(TINTS, name) ? name : '';
};
