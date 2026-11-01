// Tiny Markdown + directives for docs content. Raw HTML lines (starting with "<") pass through, so older pages keep working.
// Directives (own line):
//   {{pv {json}}}                       sound preview   (src: pad pluck arp bass drums noise voice tone seq chords grid meter rhythm (layers of hits at beat positions); fx: a block id)
//   {{piano {json}}}                    clickable keyboard {from,to,hi:[midi],play:"chord"|"scale",caption}
//   {{ample text with `code` | block-id, /learn/x/y=Label}}   "In AMPLE" box with link buttons (block ids are checked)
//   {{blocks id; id=own text; id}}      table of blocks taken from the spec (name, first sentence of its description or your text, ports)
//   {{deeper Title}} ... {{/deeper}}    collapsible "Go deeper"      {{try Title}} ... {{/try}}   collapsible exercise
const slug = (s) => s.toLowerCase().replace(/<[^>]+>/g, '').replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '');
export const inline = (s) => s.replace(/`([^`]+)`/g, '<code>$1</code>').replace(/\*\*([^*]+)\*\*/g, '<b>$1</b>').replace(/(^|[^*\w])\*([^*\s][^*]*?)\*/g, '$1<i>$2</i>').replace(/\[([^\]]+)\]\((#[^)]+)\)/g, '<a href="$2">$1</a>');
const attr = (j) => { JSON.parse(j); return j.replace(/'/g, '&#39;'); };

export function render(text, ctx) {
  const tok = [], hold = (h) => { tok.push(h); return `\n@@${tok.length - 1}@@\n`; };
  /* {{amplet Note}} ... {{/amplet}}: a complete AMPLE song, shown with the studio highlighter and a Copy button (checked by hand with `ample check`) */
  text = text.replace(/\{\{amplet([^\n}]*)\}\}\n([\s\S]*?)\n\{\{\/amplet\}\}/g, (_, note, code) => hold('<div class="pv-src" data-code="' + encodeURIComponent(code) + '" data-note="' + String(note).trim().replace(/"/g, '&quot;') + '"></div>'));
  text = text.replace(/\{\{(deeper|try) ([^\n}]+)\}\}\n([\s\S]*?)\n\{\{\/\1\}\}/g, (_, k, title, body) => hold(`<details class="${k === 'try' ? 'tryit' : 'deeper'}"><summary>${k === 'try' ? 'Try it: ' : 'Go deeper: '}${inline(title)}</summary><div class="dbody">${render(body, ctx)}</div></details>`));
  text = text.replace(/\{\{blocks ([^}]+)\}\}/g, (_, list) => hold('<table class="blocktable"><tr><th>Block</th><th>What it does</th><th>In → out</th></tr>' + list.split(';').map((e) => e.trim()).filter(Boolean).map((e) => { const [id, ...r] = e.split('='), b = ctx.blockData?.get(id.trim()); if (!b) { ctx.warn('unknown block "' + id.trim() + '"'); return ''; } const first = (b.description || '').split(/(?<=[.!?])\s/)[0], txt = r.length ? r.join('=').trim() : first.length > 190 ? first.slice(0, 187) + '...' : first, ty = (a) => a.length ? a.map((p) => p.t).filter((v, i, x) => x.indexOf(v) === i).join(' + ') : '-'; return '<tr><td><a href="#/blocks/' + b.id + '"><code>' + b.id + '</code></a></td><td>' + inline(txt) + '</td><td>' + ty(b.inputs) + ' → ' + ty(b.outputs) + '</td></tr>'; }).join('') + '</table>'));
  text = text.replace(/\{\{pv (\{.*?\})\}\}(?!\})/g, (_, j) => hold(`<div class="pv" data-pv='${attr(j)}'></div>`));
  text = text.replace(/\{\{piano (\{.*?\})\}\}(?!\})/g, (_, j) => hold(`<div class="kb" data-kb='${attr(j)}'></div>`));
  text = text.replace(/\{\{ample ([^|}]+)\|([^}]+)\}\}/g, (_, t, links) => hold(`<div class="ample-box"><b>In AMPLE</b><span>${inline(t.trim())}</span> ${links.split(',').map((l) => { l = l.trim(); const m = l.match(/^(\/[^=]+)=(.+)$/); if (m) return `<a href="#${m[1]}">${m[2]}</a>`; if (ctx.blocks && !ctx.blocks.has(l)) ctx.warn(`unknown block "${l}"`); return `<a href="#/blocks/${l}">${l}</a>`; }).join(' ')}</div>`));
  const lines = text.split('\n'), out = []; let i = 0;
  while (i < lines.length) {
    const l = lines[i];
    if (!l.trim()) { i++; continue; }
    if (/^@@\d+@@$/.test(l.trim())) { out.push(l.trim()); i++; continue; }
    if (l[0] === '<') { const r = []; while (i < lines.length && lines[i].trim() && lines[i][0] === '<') r.push(lines[i++]); out.push(r.join('\n')); continue; }
    const h = l.match(/^(#{2,3}) (.+?)(?: \{#([\w-]+)\})?$/); if (h) { const n = h[1].length; out.push(`<h${n} id="${h[3] || slug(h[2])}">${inline(h[2])}</h${n}>`); i++; continue; }
    if (/^- /.test(l)) { const r = []; while (i < lines.length && /^- /.test(lines[i])) r.push(`<li>${inline(lines[i++].slice(2))}</li>`); out.push(`<ul>${r.join('')}</ul>`); continue; }
    if (/^\d+\. /.test(l)) { const r = []; while (i < lines.length && /^\d+\. /.test(lines[i])) r.push(`<li>${inline(lines[i++].replace(/^\d+\. /, ''))}</li>`); out.push(`<ol>${r.join('')}</ol>`); continue; }
    if (/^!?> /.test(l)) { const warn = l[0] === '!', r = []; while (i < lines.length && /^!?> /.test(lines[i])) r.push(lines[i++].replace(/^!?> /, '')); out.push(`<div class="note${warn ? ' warn' : ''}">${inline(r.join(' '))}</div>`); continue; }
    if (l[0] === '|') { const rows = []; while (i < lines.length && lines[i][0] === '|') rows.push(lines[i++].split('|').slice(1, -1).map((c) => c.trim())); const body = rows.filter((r) => !r.every((c) => /^:?-+:?$/.test(c))); out.push(`<table><tr>${body[0].map((c) => `<th>${inline(c)}</th>`).join('')}</tr>${body.slice(1).map((r) => `<tr>${r.map((c) => `<td>${inline(c)}</td>`).join('')}</tr>`).join('')}</table>`); continue; }
    const p = []; while (i < lines.length && lines[i].trim() && !/^(@@\d+@@|<|#{2,3} |- |\d+\. |!?> |\|)/.test(lines[i])) p.push(lines[i++]); out.push(`<p>${inline(p.join(' '))}</p>`);
  }
  return out.join('\n').replace(/@@(\d+)@@/g, (_, n) => tok[+n]);
}
