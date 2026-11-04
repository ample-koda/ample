// A small Markdown to HTML converter for the guide (no dependencies), with the glossary links built in. @ample:guide
// Supports: headings, paragraphs, bullet and numbered lists, tables, fenced code, block quotes (callouts), horizontal rules, images, links,
// **bold**, *italic*, `code`, and the figure syntax  ![[shot:name]] / ![[shot:name|caption]].
// Words that are glossary terms become links with a tooltip, the first time they appear on a page (never in code, headings or existing links).

export const escapeHtml = (s) => String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
export const slugify = (s) => String(s).toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');

/** Plain text of some Markdown (for tooltips and search): no markup, one line. */
export function plainText(md) {
  return String(md)
    .replace(/!\[\[[^\]]*\]\]/g, '')
    .replace(/`([^`]*)`/g, '$1')
    .replace(/!?\[([^\]]*)\]\([^)]*\)/g, '$1')
    .replace(/[*_>#]/g, '')
    .replace(/\s+/g, ' ')
    .trim();
}

/**
 * Builds the matcher for the glossary.
 * @param {{ id: string, names: string[] }[]} terms
 */
export function termMatcher(terms) {
  const byName = new Map();
  for (const t of terms) for (const n of t.names) byName.set(n.toLowerCase(), t);
  const names = [...byName.keys()].sort((a, b) => b.length - a.length);
  if (!names.length) return { regex: null, byName };
  const pattern = names.map((n) => n.replace(/[.*+?^${}()|[\]\\]/g, '\\$&').replace(/\s+/g, '\\s+')).join('|');
  return { regex: new RegExp(`(?<![\\w-])(${pattern})(?![\\w-])`, 'gi'), byName };
}

/**
 * Inline Markdown to HTML.
 * @param {string} text
 * @param {{ matcher?: ReturnType<typeof termMatcher>, seen?: Set<string>, onTerm?: (id: string) => void, skipTerm?: string, link?: (target: string) => string }} ctx
 */
export function inline(text, ctx = {}) {
  const hold = [];
  const keep = (html) => { hold.push(html); return `\u0000${hold.length - 1}\u0000`; };
  let s = escapeHtml(text);
  s = s.replace(/`([^`]+)`/g, (_, code) => keep(`<code>${code}</code>`));
  s = s.replace(/!\[([^\]]*)\]\(([^)\s]+)\)/g, (_, alt, src) => keep(`<img src="${src}" alt="${alt}">`));
  s = s.replace(/\[([^\]]+)\]\(([^)\s]+)\)/g, (_, label, target) => keep(`<a href="${ctx.link ? ctx.link(target) : target}">${label}</a>`));
  if (ctx.matcher?.regex) {
    s = s.replace(ctx.matcher.regex, (m) => {
      const term = ctx.matcher.byName.get(m.toLowerCase().replace(/\s+/g, ' '));
      if (!term || term.id === ctx.skipTerm || ctx.seen?.has(term.id)) return m;
      ctx.seen?.add(term.id);
      ctx.onTerm?.(term.id);
      return keep(`<a class="term" href="glossary.html#${term.id}" data-term="${term.id}">${m}</a>`);
    });
  }
  s = s.replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>').replace(/(^|[^*\w])\*([^*]+)\*(?![*\w])/g, '$1<em>$2</em>');
  return s.replace(/\u0000(\d+)\u0000/g, (_, i) => hold[Number(i)]);
}

/**
 * Block Markdown to HTML. Returns { html, headings, shots }.
 * @param {string} md
 * @param {{ matcher?: any, seen?: Set<string>, onTerm?: (id: string) => void, skipTerm?: string, link?: (t: string) => string, shot?: (name: string, caption: string) => string }} ctx
 */
export function render(md, ctx = {}) {
  const lines = String(md).replace(/\r\n/g, '\n').split('\n');
  const out = [], headings = [], shots = [], audio = [];
  const used = new Set();
  let i = 0;
  const para = (buf) => { if (buf.length) out.push(`<p>${inline(buf.join(' '), ctx)}</p>`); };
  while (i < lines.length) {
    const line = lines[i];
    if (!line.trim()) { i++; continue; }
    const fence = /^```(\w*)\s*$/.exec(line);
    if (fence) {
      const code = [];
      i++;
      while (i < lines.length && !/^```\s*$/.test(lines[i])) code.push(lines[i++]);
      i++;
      out.push(`<pre class="code"><code>${escapeHtml(code.join('\n'))}</code></pre>`);
      continue;
    }
    const h = /^(#{1,4})\s+(.*)$/.exec(line);
    if (h) {
      const level = h[1].length, text = h[2].trim();
      let id = slugify(text) || 'section';
      for (let n = 2; used.has(id); n++) id = `${slugify(text)}-${n}`;
      used.add(id);
      headings.push({ level, text, id });
      out.push(`<h${level} id="${id}">${inline(text, { link: ctx.link })}${level > 1 ? `<a class="anchor" href="#${id}" aria-label="Link to this section">#</a>` : ''}</h${level}>`);
      i++;
      continue;
    }
    if (/^(-{3,}|\*{3,})\s*$/.test(line)) { out.push('<hr>'); i++; continue; }
    const fig = /^!\[\[shot:([\w-]+)(?:\|(.*))?\]\]\s*$/.exec(line);
    if (fig) {
      shots.push(fig[1]);
      out.push(ctx.shot ? ctx.shot(fig[1], fig[2] ?? '') : `<figure><img src="img/${fig[1]}.png" alt="${escapeHtml(fig[2] ?? fig[1])}"></figure>`);
      i++;
      continue;
    }
    const sound = /^!\[\[audio:([\w-]+)(?:\|(.*))?\]\]\s*$/.exec(line);
    if (sound) {
      audio.push(sound[1]);
      out.push(ctx.audio ? ctx.audio(sound[1], sound[2] ?? '') : `<figure><audio controls src="media/${sound[1]}.mp3"></audio></figure>`);
      i++;
      continue;
    }
    if (/^>\s?/.test(line)) {
      const quote = [];
      while (i < lines.length && /^>\s?/.test(lines[i])) quote.push(lines[i++].replace(/^>\s?/, ''));
      const kind = /^\*\*(Tip|Note|Careful|Try it)\*\*/i.exec(quote[0] ?? '')?.[1]?.toLowerCase().replace(' ', '-') ?? 'note';
      out.push(`<aside class="callout ${kind}">${render(quote.join('\n'), { ...ctx, shot: undefined }).html}</aside>`);
      continue;
    }
    if (/^\|.*\|\s*$/.test(line) && /^\|[\s:|-]+\|\s*$/.test(lines[i + 1] ?? '')) {
      const cells = (row) => row.trim().replace(/^\||\|$/g, '').split('|').map((c) => c.trim());
      const head = cells(line);
      i += 2;
      const rows = [];
      while (i < lines.length && /^\|.*\|\s*$/.test(lines[i])) rows.push(cells(lines[i++]));
      out.push(`<div class="tablewrap"><table><thead><tr>${head.map((c) => `<th>${inline(c, ctx)}</th>`).join('')}</tr></thead><tbody>${rows.map((r) => `<tr>${r.map((c) => `<td>${inline(c, ctx)}</td>`).join('')}</tr>`).join('')}</tbody></table></div>`);
      continue;
    }
    const list = /^(\s*)([-*]|\d+\.)\s+/.exec(line);
    if (list) {
      const ordered = /\d/.test(list[2]);
      const items = [];
      while (i < lines.length && /^(\s*)([-*]|\d+\.)\s+/.test(lines[i])) {
        const parts = [lines[i++].replace(/^\s*([-*]|\d+\.)\s+/, '')];
        while (i < lines.length && /^\s{2,}\S/.test(lines[i]) && !/^(\s*)([-*]|\d+\.)\s+/.test(lines[i])) parts.push(lines[i++].trim());
        items.push(parts.join(' '));
      }
      out.push(`<${ordered ? 'ol' : 'ul'}>${items.map((t) => `<li>${inline(t, ctx)}</li>`).join('')}</${ordered ? 'ol' : 'ul'}>`);
      continue;
    }
    const buf = [];
    while (i < lines.length && lines[i].trim() && !/^(#{1,4}\s|```|>\s?|\|.*\||(\s*)([-*]|\d+\.)\s+|!\[\[(?:shot|audio):)/.test(lines[i])) buf.push(lines[i++].trim());
    if (!buf.length) { buf.push(lines[i++].trim()); }
    para(buf);
  }
  return { html: out.join('\n'), headings, shots, audio };
}
