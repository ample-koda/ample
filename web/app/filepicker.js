import { api } from './api.js';
import { renderProjectFileList } from './filelist.js';
import { renderProjectTree } from './projecttree.js';
import { ask, el, rescueMenu, showMenu, toast } from './ui.js';
import { parentFolder } from '../lib/filepicker-model.mjs';
import { dragHasFiles } from './dragfile.js';

const conflictModes = [
  { label: 'Refuse', value: 'refuse' },
  { label: 'Overwrite', value: 'overwrite' },
  { label: 'Keep both', value: 'rename' },
  { label: 'Overwrite and record', value: 'overwrite-record', primary: true },
];

async function uploadOne(file, folder, applyConflict, onProgress) {
  let mode = applyConflict.value || 'refuse';
  let result = await api.uploadProject(file, folder, mode, onProgress);
  if (!result.conflict) return result;
  const applyAll = el('input', { type: 'checkbox', 'aria-label': 'Apply this choice to all uploads' });
  const applyLabel = el('label', {}, [applyAll, document.createTextNode(' Apply this choice to all uploads')]);
  const choice = await ask('File already exists', `${file.name} · existing ${result.sizeBytes} bytes · new ${file.size} bytes`, conflictModes, [applyLabel]);
  if (applyAll.checked) applyConflict.value = choice || 'refuse';
  if (!choice || choice === 'refuse') return undefined;
  return api.uploadProject(file, folder, choice, onProgress);
}

export async function uploadProjectFiles(fileList, folder, onProgress) {
  const applyConflict = { value: null };
  const results = [];
  for (const file of fileList) {
    onProgress(file, 0, file.size, 'uploading');
    const result = await uploadOne(file, folder, applyConflict, (current, loaded, total) => onProgress(current, loaded, total, 'uploading'));
    onProgress(file, result ? file.size : 0, file.size, result ? 'complete' : 'skipped');
    if (result?.converted) toast(`${file.name} is also saved as ${result.converted} (blocks play WAV files).`);
    else if (result?.convertError) toast(`${file.name} was uploaded, but it could not be converted to WAV: ${result.convertError}`);
    results.push(result);
  }
  return results;
}

export async function openProjectFilePicker(current, accept, mode) {
  const dialog = el('dialog', { className: 'project-file-picker', 'aria-labelledby': 'project-picker-title' });
  const title = el('h2', { id: 'project-picker-title', textContent: mode === 'folder' ? 'Choose a project folder' : 'Choose a project file' });
  const search = el('input', { type: 'search', placeholder: 'Search files', 'aria-label': 'Search files' });
  const tree = el('div', { className: 'project-tree' });
  const files = el('div', { className: `project-file-list${mode === 'folder' ? ' folder-picker-list' : ''}` });
  const detail = el('p', { className: 'project-file-detail', textContent: 'Select a file to see its details.' });
  const uploadStatus = el('div', { className: 'project-upload-progress', 'aria-live': 'polite' });
  const upload = el('input', { type: 'file', multiple: true, 'aria-label': 'Upload files' });
  const newFolder = el('button', { type: 'button', textContent: 'New folder' });
  const choose = el('button', { type: 'button', className: 'primary', textContent: 'Choose', disabled: mode !== 'folder' || !current });
  const noFile = el('button', { type: 'button', textContent: mode === 'folder' ? 'Use no folder' : 'Use no file' });
  const cancel = el('button', { type: 'button', textContent: 'Cancel' });
  // compact rows (a name, no preview) and one fixed layout: search, the two lists that fill the height, and a foot with the details and the actions
  files.dataset.rowHeight = '34';
  upload.hidden = true;
  const uploadButton = el('button', { type: 'button', textContent: 'Upload...', title: 'Add files from this computer to the open folder (or drop them on the list)', onclick: () => upload.click() });
  const foot = el('div', { className: 'project-picker-foot' }, [detail, uploadStatus, el('div', { className: 'project-picker-actions' }, [uploadButton, newFolder, noFile, el('span', { className: 'sp' }), cancel, choose]), upload]);
  dialog.append(title, search, el('div', { className: 'project-picker-body' }, [tree, files]), foot);
  document.body.append(dialog);

  let selected = current || '';
  let activeFolder = parentFolder(selected);
  let activeItems = [];
  let roots = [];
  const loadChildren = async (folder) => (await api.get(`/api/project/tree?path=${encodeURIComponent(folder.path)}&depth=1`)).children;
  const refresh = async () => {
    const listing = await api.get(`/api/project/tree?path=${encodeURIComponent(activeFolder)}&depth=1`);
    activeItems = listing.children;
    if (mode === 'folder') {
      files.setAttribute('role', 'listbox');
      files.replaceChildren(...activeItems.filter((item) => item.isDir).map((item) => {
        const row = el('button', { type: 'button', role: 'option', 'data-path': item.path, 'aria-selected': String(item.path === selected), className: `file-list-row${item.path === selected ? ' sel' : ''}`, textContent: item.name });
        row.onclick = () => selectFolder(item);
        return row;
      }));
      return;
    }
    renderProjectFileList(files, activeItems, search.value, accept, selected, chooseFile, showListMenu);
  };
  const selectFolder = async (folder) => { activeFolder = folder.path; if (mode === 'folder') { selected = folder.path; choose.disabled = false; detail.textContent = `Selected folder: ${folder.path}`; } renderTree(); await refresh(); };
  const renderTree = () => renderProjectTree(tree, roots, selectFolder, async (folder) => { folder.children ||= await loadChildren(folder); return folder.children; }, mode === 'folder' ? selected : activeFolder, showItemMenu);
  const showItemMenu = (event, item) => showMenu(event.clientX, event.clientY, [
    { label: 'Rename or move...', fn: () => moveItem(item) },
    { label: 'Delete', danger: true, fn: () => deleteItem(item) },
  ]);
  /** Right click in the list: on a folder, or on an empty spot, it can get a new folder; on an item it can also be renamed, moved or deleted. */
  const showListMenu = (event, item) => showMenu(event.clientX, event.clientY, [
    { label: 'Add', children: [{ label: 'Folder', fn: () => createFolderIn(item?.isDir ? item.path : activeFolder) }] },
    ...(item ? ['-', { label: 'Rename or move...', fn: () => moveItem(item) }, { label: 'Delete', danger: true, fn: () => deleteItem(item) }] : []),
  ]);
  const moveItem = async (item) => {
    const destination = el('input', { type: 'text', 'aria-label': 'New project path', value: item.path, maxLength: 256 });
    const accepted = await ask('Rename or move', `Enter a new project-relative path for ${item.name}.`, [{ label: 'Move', value: true, primary: true }, { label: 'Cancel', value: false }], [destination]);
    if (!accepted || !destination.value.trim() || destination.value.trim() === item.path) return;
    try { await api.moveProjectPath(item.path, destination.value.trim()); await refresh(); renderTree(); }
    catch (error) { toast(error.message); }
  };
  const deleteItem = async (item) => {
    const accepted = await ask('Delete project item', `Delete ${item.path}? Folders must be empty.`, [{ label: 'Delete', value: true, primary: true }, { label: 'Cancel', value: false }]);
    if (!accepted) return;
    try {
      if (item.isDir) await api.deleteProjectFolder(item.path);
      else await api.deleteProjectFile(item.path);
      if (selected === item.path) { selected = ''; choose.disabled = true; }
      await refresh(); renderTree();
    } catch (error) { toast(error.message); }
  };
  const chooseFile = async (item) => {
    if (item.isDir) { await selectFolder(item); return; }   // a folder in the list opens it
    selected = item.path;
    choose.disabled = false;
    detail.replaceChildren(
      el('span', { textContent: `${item.path} · ${item.sizeBytes} bytes · ${new Date(item.modified).toLocaleString()}` }),
    );
    renderProjectFileList(files, activeItems, search.value, accept, selected, chooseFile, showListMenu);
    try {
      const usage = await api.get(`/api/project/usage?path=${encodeURIComponent(item.path)}`);
      if (selected === item.path) detail.append(el('span', { className: 'project-file-users', textContent: usage.users.length ? `Used by ${usage.users.map((user) => `${user.block} (${user.path})`).join(', ')}` : 'Not referenced by a song or module.' }));
    } catch (error) { if (selected === item.path) detail.append(el('span', { className: 'project-file-users', textContent: error.message })); }
  };
  const revealCurrent = async () => {
    const parts = activeFolder.split('/').filter(Boolean);
    let level = roots;
    const revealed = [];
    for (const part of parts) {
      const folder = level.find((item) => item.isDir && item.name === part);
      if (!folder) { activeFolder = revealed.join('/'); selected = ''; choose.disabled = true; break; }
      folder.expanded = true;
      revealed.push(part);
      folder.children ||= await loadChildren(folder);
      level = folder.children;
    }
    renderTree();
    await refresh();
    if (selected) {
      const row = [...files.querySelectorAll('[role=option]')].find((item) => item.dataset.path === selected);
      row?.scrollIntoView({ block: 'nearest' });
      const item = activeItems.find((entry) => entry.path === selected);
      if (item) chooseFile(item);
    }
  };
  const uploadFiles = async (fileList, into = activeFolder) => {
    const rows = new Map();
    const showProgress = (file, loaded, total, state) => {
      let row = rows.get(file);
      if (!row) {
        const label = el('span', { textContent: file.name });
        const bar = el('progress', { max: file.size || 1, value: 0, 'aria-label': `Upload ${file.name}` });
        const value = el('span', { className: 'upload-progress-value' });
        row = el('div', { className: 'project-upload-row' }, [label, bar, value]);
        rows.set(file, row); uploadStatus.append(row);
      }
      const bar = row.querySelector('progress'), value = row.querySelector('.upload-progress-value');
      bar.max = Math.max(1, total); bar.value = loaded;
      value.textContent = state === 'complete' ? 'Complete' : state === 'skipped' ? 'Skipped' : `${total ? Math.floor(loaded / total * 100) : 0}%`;
    };
    try { await uploadProjectFiles(fileList, into, showProgress); await refresh(); }
    catch (error) { detail.textContent = error.message; }
  };

  try {
    roots = (await api.get('/api/project/tree?depth=1')).children;
    renderTree();
    await revealCurrent();
  } catch (error) {
    rescueMenu(); dialog.remove();
    throw error;
  }

  dialog.ondragover = (event) => { if (dragHasFiles(event)) { event.preventDefault(); event.dataTransfer.dropEffect = 'copy'; } };
  // dropped on a folder (in the list or the tree): into that folder; anywhere else: into the open folder
  dialog.ondrop = async (event) => {
    if (!event.dataTransfer?.files.length) return;
    event.preventDefault();
    const onFolder = event.target.closest?.('.file-list-folder[data-path], .tree-row[data-path]');
    await uploadFiles(event.dataTransfer.files, onFolder ? onFolder.dataset.path : activeFolder);
  };
  dialog.onkeydown = (event) => {
    if (event.key === '/' && event.target !== search) { event.preventDefault(); search.focus(); }
    if (event.key === 'Backspace' && event.target !== search && event.target.tagName !== 'INPUT' && activeFolder) { event.preventDefault(); activeFolder = parentFolder(activeFolder); renderTree(); refresh().catch((error) => toast(error.message)); }
  };
  search.oninput = () => renderProjectFileList(files, activeItems, search.value, accept, selected, chooseFile, showListMenu);
  upload.onchange = async () => { if (upload.files) await uploadFiles(upload.files); upload.value = ''; };
  const createFolderIn = async (parent) => {
    const name = el('input', { type: 'text', 'aria-label': 'Folder name', maxLength: 128 });
    const create = await ask('New folder', `Create a folder in ${parent || 'the project root'}.`, [{ label: 'Create', value: true, primary: true }, { label: 'Cancel', value: false }], [name]);
    if (!create || !name.value.trim()) return;
    try {
      await api.createProjectFolder(parent ? `${parent}/${name.value.trim()}` : name.value.trim());
      roots = (await api.get('/api/project/tree?depth=1')).children;
      await revealCurrent();
    } catch (error) { toast(error.message); }
  };
  newFolder.onclick = () => createFolderIn(activeFolder);
  return new Promise((resolve) => {
    choose.onclick = () => { dialog.close(); rescueMenu(); dialog.remove(); resolve(selected || undefined); };
    noFile.onclick = () => { dialog.close(); rescueMenu(); dialog.remove(); resolve(''); };
    cancel.onclick = () => { dialog.close(); rescueMenu(); dialog.remove(); resolve(undefined); };
    dialog.oncancel = (event) => { event.preventDefault(); dialog.close(); rescueMenu(); dialog.remove(); resolve(undefined); };
    dialog.showModal();
    search.focus();
  });
}
