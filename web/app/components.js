// Components in the palette: the reusable typed blocks a song can see (its own and the imported ones) become dynamic templates next to the built-in ones.
// The server (/api/components) knows the versions and seals; this file only turns them into the template shape the palette and the panels already read.
import { api } from './api.js';
import { S, applyText, notify, aliasComponentTakes } from './store.js';
import { ask, toast } from './ui.js';
import { cardHeightOf } from './component-ui.js';

const PREFIX = 'Components';

/** The stream type a component port has on the board (Notes is the midi stream). */
export function portType(type) {
  return type === 'Notes' ? 'midi' : String(type || '').toLowerCase();
}

/** How a seal is said in a badge. */
export function sealLabel(version) {
  if (!version) return '';
  if (version.seal === 'sealed') return version.signedBy ? `sealed · signed by ${version.signedBy}${version.signatureValid === false ? ' (signature does not match)' : ''}` : 'sealed';
  if (version.seal === 'invalid') return `seal broken${version.sealReason ? ` (${version.sealReason})` : ''}`;
  return 'unsealed';
}

/** The version of a template a block gets for the version text of its header (1.8 gets the highest 1.8.x, nothing gets the default). */
export function versionOf(template, request) {
  const versions = template?.componentVersions || [];
  const text = String(request || '').replace(/^[v^~]/, '');
  if (!text) return versions.find((v) => v.version === template.version) || versions[0] || null;
  const matches = versions.filter((v) => v.version === text || v.version.startsWith(`${text}.`));
  return matches.sort((a, b) => b.version.localeCompare(a.version, 'en', { numeric: true }))[0] || null;
}

/** The rows the properties panel shows for a block that is a component instance: where it comes from, which version it got and what its seal says. */
export function componentRows(template, request) {
  const v = versionOf(template, request);
  const rows = [['friendly name', template.title], ['full name', template.id], ['component id', template.componentId], ['source', v ? v.origin : 'unknown']];
  rows.push(['resolved version', v ? `${v.version}${v.current ? ' (current)' : ''}${v.prerelease ? ' (pre-release)' : ''}` : `no version matches ${request || 'the default'}`]);
  if (v) rows.push(['seal', sealLabel(v)]);
  if (v?.deprecated) rows.push(['deprecated', v.deprecated + (v.replacement ? ` — use ${v.replacement}` : '')]);
  rows.splice(1, 0, ['kind', template.kind || 'Notes']);
  if ((template.kind || 'Notes') !== 'Notes') {   // Audio, or Mixed (Notes in, sound out)
    rows.push(['processors', template.processors.join(', ') || 'none']);
    rows.push(['browser preview', template.clientPreview ? 'yes' : `no (${template.noPreview.join(', ')}): the server renders it`]);
    if (template.latencyMs > 0) rows.push(['latency', `${template.latencyMs} ms (not compensated)`]);
  }
  const others = template.componentVersions.filter((x) => x !== v).map((x) => x.version);
  if (others.length) rows.push(['other versions', others.join(', ')]);
  return rows;
}

/** The template a listed component gives the palette. */
export function templateOf(c) {
  const def = c.versions.find((v) => v.version === c.defaultVersion) || c.versions[0];
  const namespace = c.path.includes('.') ? c.path.slice(0, c.path.lastIndexOf('.')) : '';
  return {
    id: c.path,
    title: c.title || c.path.slice(c.path.lastIndexOf('.') + 1),
    category: namespace ? `${PREFIX} · ${namespace}` : PREFIX,
    class: 'stable',
    version: c.defaultVersion,
    dynamic: false,   // "dynamic" is a volatile result (an AI take, the clock): a component is deterministic, so no dynamic badge, no variants or bake menu and no seed controls on its card
    component: true,
    componentId: c.id,
    componentVersions: c.versions,
    kind: c.kind || 'Notes',
    clientPreview: c.clientPreview !== false,
    noPreview: c.noPreview || [],
    latencyMs: c.latencyMs || 0,
    processors: c.processors || [],
    outputNodes: c.outputNodes || {},
    ui: c.ui || null,
    cardHeight: cardHeightOf(c.ui?.card),
    livePreview: Boolean(c.livePreview),
    noLive: c.noLive || [],
    description: [c.description, def ? sealLabel(def) : '', def?.deprecated ? `deprecated: ${def.deprecated}` : ''].filter(Boolean).join(' · '),
    inputs: c.inputs.map((i) => ({ name: i.name, type: portType(i.type), required: !i.optional, title: i.name, description: i.description || '' })),
    outputs: c.outputs.map((o) => ({ name: o.name, type: portType(o.type), required: false, title: o.name, description: o.description || '' })),
    parameters: c.properties.map(parameterOf),
  };
}

/** The metadata of a property for the fields of the panel. The server decides the kind of control from the type and the attributes (a number with MinValue/MaxValue/Step gets a slider, Options a choice, Bool a true/false choice); this only shapes it for `fieldFor`. */
export function parameterOf(p) {
  // the friendly name (#[DisplayName("Echo")]) is the label everywhere; the name stays in the tooltip
  const base = { name: p.name, label: p.title || p.name, fullName: p.name, type: p.type, default: p.default ?? '', documentation: [p.title ? `${p.title} (${p.name})` : '', p.description].filter(Boolean).join(' · ') || null, modalEditor: false, multiline: false, kind: 'text', editor: p.editor || '' };
  if (p.kind === 'number') {
    const ranged = Number.isFinite(p.min) && Number.isFinite(p.max);
    const span = ranged ? p.max - p.min : 0;
    return { ...base, kind: 'number', unit: p.unit || undefined, tight: true, ...(ranged ? { min: p.min, max: p.max, step: p.step ?? (/^Int/.test(p.type) ? 1 : span <= 1 ? 0.01 : span <= 10 ? 0.1 : 1) } : {}) };
  }
  if (p.kind === 'toggle') return { ...base, kind: 'choice', options: ['true', 'false'] };
  if (p.kind === 'choice') return { ...base, kind: 'choice', options: p.options || [], quoted: /^(String|Text)\??$/.test(p.type) };
  return base;
}

const say = (preview) => [
  preview.components.length ? `Will be written: ${preview.components.join(', ')}.` : 'Nothing new will be written.',
  preview.alreadyThere.length ? `Embedded already (same code): ${preview.alreadyThere.join(', ')}.` : '',
  preview.differs.length ? `In the song with other code (stops the embed): ${preview.differs.join(', ')}.` : '',
].filter(Boolean).join('\n');

/** The source actions of a block that is a component instance (docs/language/19-components.md, "Embedding and extraction"): embed, use the template, extract, detach. */
export function componentMenuItems(n) {
  const t = S.tpl[n.tpl];
  if (!t?.component) return [];
  const v = versionOf(t, n.ver);
  const embedded = v?.origin === 'song';
  const apply = async (what, body, done) => {
    try {
      const result = await api.post(`/api/components/${what}`, { path: S.path, ...body });
      if (result.text !== undefined) await applyText(result.text);
      toast(done || result.message);
    } catch (error) {
      toast(error.message || String(error));
    }
  };
  // an Audio component keeps its processors in a graph: show it (nodes, parameters, links), nothing is rendered for that
  const graphItems = t.kind === 'Audio' || t.kind === 'Mixed' ? [{
    label: 'Show internal graph…',
    fn: async () => {
      try {
        const g = await api.get(`/api/components/graph?path=${encodeURIComponent(S.path)}&block=${encodeURIComponent(n.id)}`);
        const lines = [
          ...g.nodes.map((node) => `${node.id}  (${node.template}@${node.version}, block ${node.block})\n    ${node.parameters.map((p) => `${p.name} ${p.value}`).join(', ') || 'defaults'}\n    ${node.inputs.map((i) => `${i.port} <- ${i.source}`).join('; ')}`),
          ...Object.entries(g.outputs).map(([name, source]) => `output ${name} <- ${source}`),
          `latency ${g.latencyMs} ms (not compensated); browser preview: ${g.clientPreview ? 'yes' : `no (${g.noPreview.join(', ')})`}`,
          ...g.issues.map((i) => `${i.severity} ${i.code} line ${i.line}: ${i.message}`),
        ];
        await ask(`${g.component} ${g.version}: ${n.title}`, lines.join('\n'), [{ label: 'Close', value: true, primary: true }]);
      } catch (error) {
        toast(error.message || String(error));
      }
    },
  }] : [];
  const embedItem = (label, whole) => ({
    label,
    fn: async () => {
      try {
        const { preview } = await api.post('/api/components/embed-preview', { path: S.path, blocks: [n.id], whole });
        const go = await ask(label.replace('…', ''), say(preview), [{ label: 'Cancel', value: false }, { label: 'Embed', value: true, primary: true }]);
        if (go) await apply('embed', { blocks: [n.id], whole });
      } catch (error) {
        toast(error.message || String(error));
      }
    },
  });
  const migrate = async (target) => {
    try {
      const { preview } = await api.post('/api/components/migrate-preview', { path: S.path, block: n.id, target });
      const lines = [
        `Impact: ${preview.impact}${preview.needsAcknowledgement ? ' (you must acknowledge it)' : ''}`,
        `Steps: ${preview.steps.map((st) => `${st.from} → ${st.to}`).join(', ')}`,
        ...preview.changes.map((c) => `${c.kind} ${c.target}${c.before != null || c.after != null ? `: ${c.before ?? '-'} → ${c.after ?? '-'}` : ''}`),
        `Notes: ${preview.notesBefore} before, ${preview.notesAfter} after`,
        ...preview.messages.map((m) => `Note: ${m}`),
        ...preview.warnings.map((w) => `Warning: ${w}`),
      ];
      const go = await ask(`Migrate ${n.title} to ${target}`, lines.join('\n'), [{ label: 'Cancel', value: false }, { label: preview.needsAcknowledgement ? 'I have looked: migrate' : 'Migrate', value: true, primary: true }]);
      if (go) await apply('migrate', { block: n.id, target, acknowledge: preview.needsAcknowledgement });
    } catch (error) {
      toast(error.message || String(error));
    }
  };
  const others = t.componentVersions.filter((x) => x !== v);
  const migrateItems = others.length ? [{ label: 'Migrate to version', children: others.map((x) => ({ label: `${x.version}${x.current ? ' (current)' : ''}${x.deprecated ? ' — deprecated' : ''}`, fn: () => migrate(x.version) })) }] : [];
  if (!embedded) return [...graphItems, ...migrateItems, embedItem('Embed dependencies in song…', false), embedItem('Embed source module in song…', true)];
  return [
    ...graphItems,
    ...migrateItems,
    { label: 'Use template source', title: 'Only when the template has exactly the same sealed code', fn: () => apply('use-template', { name: declaredName(t), version: v.version }) },
    {
      label: 'Extract to template…',
      fn: async () => {
        const input = document.createElement('input');
        input.type = 'text';
        input.value = `components/${t.id.slice(t.id.lastIndexOf('.') + 1)}.amplec`;
        const go = await ask('Extract to template', 'The component is written to this new .amplec file and the song imports it.', [{ label: 'Cancel', value: false }, { label: 'Extract', value: true, primary: true }], [input]);
        if (go) await apply('extract', { name: declaredName(t), version: v.version, file: input.value.trim() });
      },
    },
    ...(v?.seal === 'sealed' ? [{ label: 'Detach for editing', title: 'Removes the seal: it builds as a development version until it is sealed again', fn: () => apply('detach', { name: declaredName(t), version: v.version }) }] : []),
  ];
}

/** The name a component is declared with (the palette path is the exported name, which can be an alias). */
function declaredName(template) {
  return template.declaredName || template.id;
}

/** Reads the components of the open song and puts them in S.templates and S.tpl (the old ones are replaced). Returns the issues the server found. */
export async function loadComponents(path) {
  let listed;
  try {
    listed = await api.get(`/api/components?path=${encodeURIComponent(path)}`);
  } catch {
    listed = { components: [], issues: [] };
  }
  const own = listed.components.map(templateOf);
  S.templates = [...S.templates.filter((t) => !t.component), ...own];
  S.tpl = Object.fromEntries(S.templates.map((t) => [t.id, t]));
  S.tplRev = (S.tplRev || 0) + 1;   // the board's graph was made with the old templates
  S.componentIssues = listed.issues || [];
  S.componentFiles = listed.files || [];
  aliasComponentTakes();   // the instances of audio components play with the take of their output processor
  notify();
  document.dispatchEvent(new CustomEvent('ample:components-changed'));   // the palette draws its list again
  return S.componentIssues;
}
