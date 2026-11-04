import { el } from './ui.js';
import { projectItemIcon } from './fileicons.js';
import { beginFileDrag } from './dragfile.js';

export function renderProjectTree(host, roots, selectFolder, loadChildren, selectedPath, onContextMenu) {
  host.setAttribute('role', 'tree');
  host.innerHTML = '';
  const append = (items, depth) => items.filter((item) => item.isDir).forEach((item) => {
    const wrap = el('div', { className: 'tree-node' });
    const toggle = el('button', { type: 'button', className: 'tree-toggle', title: `${item.expanded ? 'Collapse' : 'Expand'} ${item.name}`, 'aria-label': `${item.expanded ? 'Collapse' : 'Expand'} ${item.name}` }, [projectItemIcon(item)]);
    const row = el('button', { type: 'button', role: 'treeitem', className: 'tree-row', draggable: Boolean(item.path), 'data-path': item.path, 'data-dir': '1', 'aria-level': depth + 1, 'aria-selected': String(item.path === selectedPath), 'aria-expanded': String(Boolean(item.expanded)) }, [el('span', { className: 'project-item-name', textContent: item.name })]);
    row.dataset.path = item.path;
    if (item.path) row.ondragstart = (event) => beginFileDrag(event, item);
    wrap.style.paddingInlineStart = `${depth * 16}px`;
    toggle.onclick = async () => {
      if (!item.children) item.children = await (loadChildren ? loadChildren(item) : selectFolder(item));
      item.expanded = !item.expanded;
      host.dispatchEvent(new CustomEvent('ample:tree-toggle', { detail: { path: item.path, expanded: item.expanded } }));
      renderProjectTree(host, roots, selectFolder, loadChildren, selectedPath, onContextMenu);
    };
    // the second click of a double click opens or folds the folder (the click event survives a redraw of the rows, a dblclick event does not)
    row.onclick = (event) => { if (event.detail >= 2) { toggle.click(); return; } selectFolder(item); };
    row.oncontextmenu = (event) => { if (onContextMenu) { event.preventDefault(); onContextMenu(event, item); } };
    row.onkeydown = async (event) => {
      const visible = [...host.querySelectorAll('[role=treeitem]')];
      const index = visible.indexOf(row);
      if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
        event.preventDefault();
        visible[Math.max(0, Math.min(visible.length - 1, index + (event.key === 'ArrowDown' ? 1 : -1)))]?.focus();
      } else if (event.key === 'ArrowRight' && !item.expanded) {
        event.preventDefault();
        toggle.click();
      } else if (event.key === 'ArrowLeft') {
        event.preventDefault();
        if (item.expanded) toggle.click();
        else visible.slice(0, index).reverse().find((candidate) => candidate.getAttribute('aria-level') < depth + 1)?.focus();
      } else if (event.key === 'Enter' || event.key === ' ') {
        event.preventDefault();
        row.click();
      }
    };
    wrap.append(toggle, row); host.append(wrap);
    if (item.expanded && item.children?.length) append(item.children, depth + 1);
  });
  append(roots, 0);
}
