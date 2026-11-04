const keyFor = (projectId, path) => `ample.source-draft:${projectId}:${path}`;

export function saveRecoveryDraft(storage, projectId, path, text, baseText, updated) {
  const previous = getRecoveryDraft(storage, projectId, path);
  storage.setItem(keyFor(projectId, path), JSON.stringify({ path, text, baseText: previous?.baseText ?? baseText, updated }));
}

export function getRecoveryDraft(storage, projectId, path) {
  try {
    const draft = JSON.parse(storage.getItem(keyFor(projectId, path)) || 'null');
    return draft && draft.path === path && typeof draft.text === 'string' && Number.isFinite(draft.updated) ? { ...draft, baseText: typeof draft.baseText === 'string' ? draft.baseText : '' } : null;
  } catch { return null; }
}

export function listRecoveryDrafts(storage, projectId) {
  const prefix = `ample.source-draft:${projectId}:`, drafts = [];
  try {
    for (let index = 0; index < storage.length; index++) {
      const key = storage.key(index);
      if (!key?.startsWith(prefix)) continue;
      try {
        const draft = JSON.parse(storage.getItem(key));
        if (draft?.path && typeof draft.text === 'string' && Number.isFinite(draft.updated)) drafts.push({ ...draft, baseText: typeof draft.baseText === 'string' ? draft.baseText : '' });
      } catch { /* one damaged browser entry does not hide the others */ }
    }
  } catch { return []; }
  return drafts.sort((a, b) => b.updated - a.updated);
}

export function removeRecoveryDraft(storage, projectId, path) {
  storage.removeItem(keyFor(projectId, path));
}
