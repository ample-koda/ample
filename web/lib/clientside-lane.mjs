// The browser preview of a sequencer: which output of the block is a lane, so that only that lane is taken from the server take (the plain take is the mix of all lanes).

/** The role to ask the server for: the lane ("lane1", "lane2", ... "lane12"), or '' for every other output (the mix of all lanes). */
export const laneRole = (output) => (/^lane\d+$/.test(String(output || '')) ? String(output) : '');
