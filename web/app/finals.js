// The Finals page: a song can deliver several finals (the full version, an edit, a teaser). Each one is rendered on purpose,
// with the estimate of the build first; everyday building stays light.
import { api } from './api.js';
import { S } from './store.js';
import { ask, el, toast } from './ui.js';

const GB = 1024 ** 3;
const size = (b) => (b >= GB ? `${(b / GB).toFixed(1)} GB` : b >= 1024 ** 2 ? `${(b / 1024 ** 2).toFixed(1)} MB` : `${Math.max(1, Math.round(b / 1024))} KB`);
const span = (spec) => (spec.bars > 0 ? `bars ${spec.fromBar} to ${spec.fromBar + spec.bars - 1}` : spec.fromBar > 1 ? `from bar ${spec.fromBar} to the end` : 'the whole song');

export async function finalsPage(body) {
  const song = S.path;
  body.append(el('h2', { textContent: 'Finals' }));
  if (!song) { body.append(el('p', { className: 'note', textContent: 'Open a song first: finals belong to a song.' })); return; }
  body.append(el('p', { className: 'note', textContent: `A final is a deliverable of ${song}: the full version, an edit or a teaser cut from the master with its own fades. Everyday building does not make them; "Render" does, on purpose, and says first how big and how long it will be.` }));
  let finals;
  try { ({ finals } = await api.get(`/api/finals?song=${encodeURIComponent(song)}`)); } catch (e) { body.append(el('p', { className: 'note', role: 'alert', textContent: `The finals could not be loaded: ${e.message}` })); toast(`The finals could not be loaded: ${e.message}`); return; }
  const table = el('table', { className: 'tbl', id: 'fin-list' });
  table.append(el('tr', {}, ['Final', 'What is kept', 'Fades', 'Last rendered file', ''].map((t) => el('th', { textContent: t }))));
  finals.forEach((f) => {
    const render = el('button', { className: 'primary', textContent: 'Render', onclick: async () => {
      const estimate = await api.get(`/api/build/estimate?path=${encodeURIComponent(song)}`).catch(() => null);
      if (estimate && estimate.ok) {
        const choice = await ask(`Render "${f.name}"`, `${estimate.text} ${estimate.fits ? '' : 'It does not fit on the drive of the takebank: free space first (Projects, Storage).'}`, estimate.fits ? [{ label: 'Render', value: 'go', primary: true }, { label: 'Not now', value: 'no' }] : [{ label: 'OK', value: 'no', primary: true }]);
        if (choice !== 'go') return;
      }
      render.disabled = true;
      render.textContent = 'Rendering...';
      try {
        const r = await api.post('/api/finals/render', { song, name: f.name });
        toast(`Final "${f.name}" is ready: ${r.path} (${size(r.bytes)}, ${Math.round(r.seconds)} s).`);
      } catch (e) { toast(`${e.code ? `${e.code}: ` : ''}${e.message}`); }
      body.replaceChildren();
      await finalsPage(body);
    } });
    const row = el('tr', {}, [
      el('td', { textContent: f.name }),
      el('td', { textContent: `${span(f)}, ${f.format}` }),
      el('td', { textContent: f.fadeInBeats || f.fadeOutBars ? `in ${f.fadeInBeats} beat(s), out ${f.fadeOutBars} bar(s)` : 'none' }),
      el('td', { textContent: f.rendered ? `${f.path} (${size(f.bytes)})` : 'not rendered yet' }),
      el('td', {}, [render, f.name === 'full' && finals.length === 1 ? '' : el('button', { textContent: 'Remove', onclick: async () => {
        if (!(await ask('Remove final', `Remove the final "${f.name}"? Its settings are deleted; the song is not changed.`, [{ label: 'Remove', value: true, primary: true }, { label: 'Cancel', value: false }]))) return;
        try { await api.post('/api/finals/delete', { song, name: f.name }); } catch (e) { toast(`Could not remove the final: ${e.message}`); return; }
        body.replaceChildren(); await finalsPage(body);
      } })]),
    ]);
    row.dataset.final = f.name;
    table.append(row);
  });
  body.append(table);

  body.append(el('h4', { textContent: 'Add a final', style: 'padding:0;margin:18px 0 4px' }));
  const name = el('input', { id: 'fin-name', placeholder: 'teaser' });
  const from = el('input', { id: 'fin-from', type: 'number', min: 1, value: '1', style: 'width:80px' });
  const bars = el('input', { id: 'fin-bars', type: 'number', min: 0, value: '0', style: 'width:80px' });
  const fadeIn = el('input', { id: 'fin-fadein', type: 'number', min: 0, value: '0', style: 'width:80px' });
  const fadeOut = el('input', { id: 'fin-fadeout', type: 'number', min: 0, value: '0', style: 'width:80px' });
  const format = el('select', { id: 'fin-format' }, [el('option', { value: 'mp3', textContent: 'mp3' }), el('option', { value: 'wav', textContent: 'wav' })]);
  const field = (label, input, hint) => el('div', { className: 'row' }, [el('label', { textContent: label, style: 'min-width:170px' }), input, el('span', { className: 'note', textContent: hint })]);
  body.append(
    field('Name', name, 'the end of the rendered file name'),
    field('First bar', from, 'where the final starts'),
    field('Bars', bars, '0 keeps everything to the end'),
    field('Fade in (beats)', fadeIn, '0 for none'),
    field('Fade out (bars)', fadeOut, '0 for none'),
    field('Format', format, ''),
    el('button', { id: 'fin-add', textContent: 'Add the final', onclick: async () => {
      try {
        await api.post('/api/finals', { song, name: name.value.trim(), fromBar: Number(from.value), bars: Number(bars.value), fadeInBeats: Number(fadeIn.value), fadeOutBars: Number(fadeOut.value), format: format.value });
        body.replaceChildren();
        await finalsPage(body);
      } catch (e) { toast(`${e.code ? `${e.code}: ` : ''}${e.message}`); }
    } }),
  );
}
