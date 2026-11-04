// The tooltip of the source editor for what is under the pointer: the signature of a helper or a built-in, its doc comment and the notes of its parameters. The text is the markdown that the
// language server gives for a hover (or the same text built in the browser when the server cannot be reached). @ample:doc-tip

import { markdownBlocks } from '../lib/doc-comments.mjs';

let tip = null;

const piece = (part) => {
  if (part.code) { const c = document.createElement('code'); c.textContent = part.text; return c; }
  if (part.bold) { const b = document.createElement('b'); b.textContent = part.text; return b; }
  return document.createTextNode(part.text);
};

/** Shows the tooltip with the markdown near a point (clientX, clientY); it stays inside the window. */
export function showDocTip(markdown, x, y) {
  if (!tip) { tip = document.createElement('div'); tip.id = 'se-doctip'; tip.setAttribute('role', 'tooltip'); document.body.append(tip); }
  // what kind of thing it is (helper (pure), dynamic helper, constant, type alias, Built in.) is a badge at the top right, not a line at the bottom
  const blocks = markdownBlocks(markdown);
  const kindAt = blocks.findIndex((b) => b.type === 'p' && b.parts.length === 1 && /^(helper \(pure\)|dynamic helper(: .*)?|constant|type alias|Built in\.|let|var|parameter|loop variable|property|input|output|component(?: · .*)?)$/s.test(b.parts[0].text));
  let badge = null;
  if (kindAt >= 0) {
    const text = blocks[kindAt].parts[0].text, colon = text.indexOf(': ');
    badge = text.replace(/\.$/, '').slice(0, colon > 0 ? colon : undefined);
    if (colon > 0) blocks[kindAt] = { type: 'p', parts: [{ text: text.slice(colon + 2) }] }; else blocks.splice(kindAt, 1);
  }
  const nodes = blocks.map((block) => {
    if (block.type === 'code') { const pre = document.createElement('pre'); pre.textContent = block.text; return pre; }
    if (block.type === 'list') { const ul = document.createElement('ul'); for (const item of block.items) { const li = document.createElement('li'); li.append(...item.map(piece)); ul.append(li); } return ul; }
    // the value of a variable ("= `code`") can be very long: a block of its own that wraps and is cut off after a few lines
    if (block.parts.length === 2 && block.parts[0].text === '= ' && block.parts[1].code) {
      const value = document.createElement('div');
      value.className = 'dt-value';
      const sign = document.createElement('span');
      sign.textContent = '=';
      const code = document.createElement('code');
      code.textContent = block.parts[1].text;
      value.append(sign, code);
      return value;
    }

    const p = document.createElement('p');
    p.append(...block.parts.map(piece));
    return p;
  });
  if (badge) { const b = document.createElement('span'); b.className = 'dt-badge'; b.textContent = badge; nodes.unshift(b); }
  tip.replaceChildren(...nodes);
  tip.style.display = 'block';
  const width = tip.offsetWidth, height = tip.offsetHeight;
  tip.style.left = `${Math.max(8, Math.min(x + 14, innerWidth - width - 8))}px`;
  // above the pointer (like the hover of an editor), so that it does not cover the tip of an error under the line; below when there is no room above
  tip.style.top = `${y - height - 14 >= 8 ? y - height - 14 : Math.min(y + 18, Math.max(8, innerHeight - height - 8))}px`;
}

/** Hides the tooltip. */
export function hideDocTip() { if (tip) tip.style.display = 'none'; }

/** Whether the tooltip is showing. */
export const docTipShown = () => !!tip && tip.style.display === 'block';
