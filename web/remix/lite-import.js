// Adding files to the open project of Remix: dropped on the page (also whole folders), or picked with the Upload button (that is the way on a phone, which has no drag and drop). A .zip asks what to do
// with it (add its files to this project, or open it as a project of its own). Everything shows its progress and has a Cancel that puts the project back as it was, and the whole project can be
// exported as a .zip. The page (lite.js) gives this module what it needs through `deps`.

import { checkFile } from './lite-paths.mjs';
import { readZip, stripCommonRoot } from './lite-unzip.mjs';
import { downloadZip } from './lite-share.js';

const isZip = (name) => /\.zip$/i.test(name);
const size = (bytes) => (bytes < 1024 ? `${bytes} B` : bytes < 1048576 ? `${Math.round(bytes / 1024)} KB` : `${(bytes / 1048576).toFixed(1)} MB`);

/** What was dropped as [{file, path}]: the files, and the files inside dropped folders with the path inside the folder. */
export async function collectDropped(dataTransfer) {
  const entries = [...(dataTransfer.items ?? [])].map((item) => item.webkitGetAsEntry?.()).filter(Boolean);   // read now: the drop is over when the first await comes back
  if (!entries.length) return [...dataTransfer.files].map((file) => ({ file, path: file.name }));
  const found = [];
  const walk = async (entry, prefix) => {
    if (entry.isFile) {
      found.push({ file: await new Promise((resolve, reject) => entry.file(resolve, reject)), path: prefix + entry.name });
    } else if (entry.isDirectory) {
      const reader = entry.createReader();
      for (;;) {
        const batch = await new Promise((resolve, reject) => reader.readEntries(resolve, reject));
        if (!batch.length) break;
        for (const child of batch) await walk(child, `${prefix}${entry.name}/`);
      }
    }
  };
  for (const entry of entries) await walk(entry, '');
  return found;
}

/**
 * @param {object} deps
 * @param {() => ({id: string, name: string} | null)} deps.project the open project
 * @param {() => object} deps.store the store of the page
 * @param {() => object} deps.limits the limits
 * @param {() => Set<string>} deps.taken the lower case paths that the project has
 * @param {(path: string, taken: Set<string>) => string} deps.targetFor the path for a new file (a name that is not taken, in the right folder)
 * @param {(entries: Array<{path: string, text?: string}>) => Promise<void>} deps.added called after files were added
 * @param {(name: string, files: Array<{path: string, text?: string, blob?: Blob}>) => Promise<void>} deps.openAsProject makes a project of these files and opens it
 * @param {() => Promise<Array<{path: string, text?: string, blob?: Blob}>>} deps.snapshot all files of the open project
 * @param {(text: string, kind?: string) => void} deps.toast
 * @param {(path: string) => boolean} deps.isText
 */
export function createImporter(deps) {
  const $ = (selector) => document.querySelector(selector);
  const busy = { on: false };

  /** The progress window: a line, a bar and Cancel. */
  function progress(title) {
    const box = $('#lite-import-dialog');
    const state = { cancelled: false };
    $('#lite-import-title').textContent = title;
    $('#lite-import-line').textContent = '';
    $('#lite-import-bar').value = 0;
    $('#lite-import-cancel').disabled = false;
    $('#lite-import-cancel').onclick = () => { state.cancelled = true; $('#lite-import-cancel').disabled = true; $('#lite-import-line').textContent = 'Cancelling...'; };
    box.oncancel = (event) => { event.preventDefault(); $('#lite-import-cancel').click(); };
    if (!box.open) box.showModal();
    return {
      get cancelled() { return state.cancelled; },
      step(done, total, text) { $('#lite-import-bar').value = total ? done / total : 0; $('#lite-import-line').textContent = text; },
      close() { if (box.open) box.close(); },
    };
  }

  /** Asks what to do with a zip: add its files to the open project, or open it as a project. Resolves to 'add', 'project' or null (cancelled). */
  function chooseZip(name, summary, canAdd) {
    const box = $('#lite-zip-dialog');
    return new Promise((resolve) => {
      $('#lite-zip-name').textContent = name;
      $('#lite-zip-summary').textContent = summary;
      const add = box.querySelector('input[value="add"]'), project = box.querySelector('input[value="project"]');
      add.disabled = !canAdd;
      (canAdd ? add : project).checked = true;
      $('#lite-zip-add-text').textContent = canAdd ? `Add its files to the open project (${deps.project().name})` : 'Add its files to the open project (there is no open project)';
      const finish = (value) => { box.close(); resolve(value); };
      $('#lite-zip-go').onclick = () => finish(box.querySelector('input[name="lite-zip-mode"]:checked').value);
      $('#lite-zip-cancel').onclick = () => finish(null);
      box.oncancel = (event) => { event.preventDefault(); finish(null); };
      box.showModal();
    });
  }

  /** Puts files into the open project one by one, with progress; a Cancel (or a problem) removes what was added so far. `items` is [{path, size, load()}]. */
  async function addToProject(items, title) {
    const project = deps.project();
    const store = deps.store();
    const limits = deps.limits();
    const taken = deps.taken();
    const addedPaths = [];
    const addedEntries = [];
    const skipped = [];
    const bar = progress(title);
    try {
      for (let i = 0; i < items.length; i++) {
        if (bar.cancelled) throw Object.assign(new Error('Cancelled.'), { cancelled: true });
        const item = items[i];
        bar.step(i, items.length, `${i + 1} of ${items.length}: ${item.path} (${size(item.size)})`);
        const path = deps.targetFor(item.path, taken);
        try { checkFile(path, item.size, limits); } catch (error) { skipped.push(String(error?.message ?? error)); continue; }
        const data = await item.load();
        await store.writeFile(project.id, path, deps.isText(path) ? { text: typeof data === 'string' ? data : await data.text() } : { blob: data, mime: data.type ?? '' });
        taken.add(path.toLowerCase());
        addedPaths.push(path);
        addedEntries.push(deps.isText(path) ? { path, text: typeof data === 'string' ? data : await data.text() } : { path });
      }

      bar.step(items.length, items.length, 'Done.');
      return { added: addedEntries, skipped };
    } catch (error) {
      for (const path of addedPaths) await store.deleteFile(project.id, path).catch(() => {});   // the project is as it was
      if (addedPaths.length) await deps.added([]);
      if (error?.cancelled) return { added: [], skipped, cancelled: true };
      throw error;
    } finally {
      bar.close();
    }
  }

  /** Handles one zip: asks, then adds or opens. */
  async function handleZip({ file, path }) {
    let zip;
    try { zip = await readZip(file); } catch (error) { deps.toast(`${file.name}: ${error?.message ?? error}`, 'error'); return; }
    const project = deps.project();
    const names = stripCommonRoot(zip.entries.map((e) => e.path));
    const files = zip.entries.map((e, i) => ({ ...e, path: names[i] }));
    const total = files.reduce((sum, f) => sum + f.size, 0);
    const limits = deps.limits();
    if (files.length > limits.maxFiles || total > limits.maxProjectBytes) { deps.toast(`${file.name}: this .zip holds ${files.length} files (${size(total)}); a Remix project holds at most ${limits.maxFiles} files and ${size(limits.maxProjectBytes)}.`, 'error'); return; }
    const usable = files.filter((f) => { try { checkFile(f.path, f.size, limits); return true; } catch { return false; } });
    const summary = `${files.length} file${files.length === 1 ? '' : 's'} (${size(total)})${usable.length < files.length ? `; ${files.length - usable.length} of them cannot be used in Remix (other kinds of files, or too big) and are skipped` : ''}.`;
    const choice = await chooseZip(file.name, summary, Boolean(project));
    if (!choice) return;
    if (choice === 'add') {
      const result = await addToProject(usable.map((f) => ({ path: f.path, size: f.size, load: async () => new Blob([await f.read()]) })), `Adding the files of ${file.name}`);
      if (result.cancelled) { deps.toast('Cancelled: nothing was added.'); return; }
      await deps.added(result.added);
      deps.toast(`${result.added.length} file${result.added.length === 1 ? '' : 's'} of ${file.name} added.${result.skipped.length || files.length > usable.length ? ` ${files.length - usable.length + result.skipped.length} skipped.` : ''}`);
      return;
    }

    // as a project of its own
    const bar = progress(`Opening ${file.name} as a project`);
    try {
      const made = [];
      for (let i = 0; i < usable.length; i++) {
        if (bar.cancelled) { deps.toast('Cancelled: no project was made.'); return; }
        bar.step(i, usable.length, `${i + 1} of ${usable.length}: ${usable[i].path} (${size(usable[i].size)})`);
        const bytes = await usable[i].read();
        made.push(deps.isText(usable[i].path) ? { path: usable[i].path, text: new TextDecoder().decode(bytes) } : { path: usable[i].path, blob: new Blob([bytes]) });
      }

      bar.step(usable.length, usable.length, 'Opening the project...');
      await deps.openAsProject(file.name.replace(/\.zip$/i, '') || 'Imported project', made);
    } catch (error) {
      deps.toast(`${file.name}: ${error?.message ?? error}`, 'error');
    } finally {
      bar.close();
    }
  }

  /** Everything that was dropped or picked: [{file, path}]. */
  async function run(candidates) {
    if (busy.on || candidates.length === 0) return;
    busy.on = true;
    try {
      const zips = candidates.filter((c) => isZip(c.file.name));
      const plain = candidates.filter((c) => !isZip(c.file.name));
      if (plain.length) {
        if (!deps.project()) { deps.toast('Make a project or open a demo first.'); }
        else {
          const result = await addToProject(plain.map((c) => ({ path: c.path, size: c.file.size, load: async () => c.file })), `Adding ${plain.length} file${plain.length === 1 ? '' : 's'}`);
          if (result.cancelled) deps.toast('Cancelled: nothing was added.');
          else {
            await deps.added(result.added);
            for (const problem of result.skipped.slice(0, 3)) deps.toast(problem, 'error');
            if (result.added.length) deps.toast(`${result.added.length} file${result.added.length === 1 ? '' : 's'} added. Samples go to the samples folder: name it in your song, for example kit samples/kit.`);
          }
        }
      }

      for (const zip of zips) await handleZip(zip);
    } catch (error) {
      deps.toast(String(error?.message ?? error), 'error');
    } finally {
      busy.on = false;
    }
  }

  /** The drag and drop of the whole page, with a veil that says what happens. */
  function enableDrop() {
    const veil = $('#lite-drop');
    let depth = 0;
    const hasFiles = (event) => [...(event.dataTransfer?.types ?? [])].includes('Files');
    document.addEventListener('dragenter', (event) => { if (hasFiles(event)) { depth++; veil.hidden = false; } });
    document.addEventListener('dragover', (event) => { if (hasFiles(event)) event.preventDefault(); });
    document.addEventListener('dragleave', (event) => { if (hasFiles(event) && --depth <= 0) { depth = 0; veil.hidden = true; } });
    document.addEventListener('drop', async (event) => {
      if (!hasFiles(event)) return;
      event.preventDefault();
      depth = 0;
      veil.hidden = true;
      run(await collectDropped(event.dataTransfer));
    });
  }

  /** Downloads the whole project as a .zip. The .zip is made in this browser and handed over as a local file: there is no address on the internet that others could be given. */
  async function exportZip() {
    const project = deps.project();
    if (!project) { deps.toast('Make a project or open a demo first.'); return; }
    try {
      await downloadZip(await deps.snapshot(), project.name);
      deps.toast('The .zip was made in your browser and is only yours: it is a file, not a link. To let others open your song, use Share.');
    } catch (error) {
      deps.toast(String(error?.message ?? error), 'error');
    }
  }

  return { run, enableDrop, exportZip, pick: (fileList) => run([...fileList].map((file) => ({ file, path: file.name }))) };
}
