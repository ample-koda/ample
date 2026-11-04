// Steer a parameter with a control: a list of the controls in the song (the blocks that make one), written as a connection (from block.output -> parameter).
import { ask, el } from './ui.js';
import { S, edit, flat } from './store.js';

/** The controls of the song: [{ block, output, label }] (an output of a block whose type starts with control). */
export function controlsOf(nodes) {
  return nodes.flatMap((n) => n.outs.filter((o) => String(o[1]).startsWith('control')).map((o) => ({ block: n.id, output: o[0], type: o[1], label: `${n.title}.${o[0]}${o[1] === 'control' ? '' : ` (${String(o[1]).slice(8, -1)})`}` })));
}

/**
 * Opens the chooser for one parameter of a block and writes the answer.
 * @param {{id: string, title: string}} node The block.
 * @param {string} parameter The parameter.
 */
export async function chooseControl(node, parameter) {
  const nodes = flat().nodes;
  const controls = controlsOf(nodes).filter((c) => c.block !== node.id);
  const current = (S.doc?.blocks || []).find((b) => b.name === node.id)?.froms?.find((f) => f.input === parameter);
  if (!controls.length) { await ask('No control yet', 'Add a follow, curve or map block first: it makes the control that can steer this parameter.', [{ label: 'OK', value: true, primary: true }]); return; }
  const select = el('select', { 'aria-label': 'Control' }, controls.map((c) => el('option', { value: `${c.block}\u0000${c.output}`, textContent: c.label, selected: current && current.block === c.block && current.output === c.output })));
  const buttons = [{ label: 'Cancel', value: false }];
  if (current) buttons.push({ label: 'Disconnect', value: 'off' });
  buttons.push({ label: 'Steer', value: true, primary: true });
  const answer = await ask(`Steer ${parameter}`, `${node.title} \u00b7 ${parameter} follows the control you choose. A control without a unit is scaled to the range of the parameter; one with a unit (Hz, dB) only fits a parameter in that unit.`, buttons, [select]);
  if (answer === 'off') { await edit([{ type: 'disconnect', to: node.id, input: parameter }]); return; }
  if (!answer) return;
  const [block, output] = select.value.split('\u0000');
  await edit([...(current ? [{ type: 'disconnect', to: node.id, input: parameter }] : []), { type: 'connect', from: block, output, to: node.id, input: parameter }]);
}
