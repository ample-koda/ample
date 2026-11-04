// One way to say "there are changes" in every editor (plan project-panel PP07). Pure.
//
// A file the song is made of is in one of three states:
//   typed:   text was typed in an editor and not applied yet (only the code editor has this state)
//   changed: the working copy differs from the saved file (an edit was applied, or made in a block editor)
//   saved:   the working copy is the saved file

/** The state of the open file. typed: text in an editor that is not applied; changed: the working copy differs from the saved file. */
export function fileState({ typed = false, changed = false } = {}) {
  if (typed) return { id: 'typed', label: 'typing, not applied', title: 'Text was typed that is not applied to the song yet. Apply it (Ctrl+Enter) or Save.', mark: '●' };
  if (changed) return { id: 'changed', label: 'changed', title: 'The working copy differs from the saved file. Save writes it to the file.', mark: '●' };
  return { id: 'saved', label: 'saved', title: 'The file is saved.', mark: '' };
}

/** Which entries of the save menu can be used in a state (the menu shows the others greyed). */
export function saveMenuState(state, { hasSong = true, hasTake = false } = {}) {
  return {
    save: hasSong && state.id !== 'saved',
    saveAs: hasSong,
    revert: hasSong && state.id !== 'saved',
    export: true,
    renderBlock: hasTake,
    show: hasSong,
    copy: hasSong,
  };
}
