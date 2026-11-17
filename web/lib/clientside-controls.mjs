// Controls in the client preview (Build and play preview): which parameters of a block follow a control. A control is a number over time made by
// curve, map or follow (ControlBlocks.cs); the server turns it into the points of an automation curve for the parameter (ControlBinding.Resolve in
// ControlCurve.cs): a control link to the parameter (`from traffic.level -> cutoff`) or an expression in the parameter that reads a control
// (`cutoff 600 + 3000 * traffic.level`). Pure, tested in node.

const CONTROL_SOURCES = new Set(['curve', 'map', 'follow']);
const unquote = (text) => String(text ?? '').trim().replace(/^"|"$/g, '');

/**
 * The names of the parameters of a block that follow a control, in the order of the block.
 * @param {{id:string, params:Record<string,string>}} node the block
 * @param {{id:string, tpl:string}[]} nodes the blocks of the graph
 * @param {{from:string, to:string, inName:string, type:string, disabled?:boolean}[]} links the links of the graph
 * @returns {string[]}
 */
export function controlledParams(node, nodes, links) {
  if (!node) return [];
  const sources = new Set(nodes.filter((n) => CONTROL_SOURCES.has(n.tpl)).map((n) => n.id));
  const found = new Set();
  for (const link of links) if (link.to === node.id && link.type === 'control' && !link.disabled && sources.has(link.from)) found.add(link.inName);
  for (const [name, value] of Object.entries(node.params || {})) {
    const text = unquote(value);
    // an expression that reads block.output of a control block (traffic.level, wind.gate)
    for (const m of text.matchAll(/(?:^|[^\w.-])([A-Za-z_][\w-]*)\.([A-Za-z_]\w*)\b/g)) if (sources.has(m[1])) found.add(name);
  }
  return [...found];
}
