/** The files that match the text and the accepted extensions; with withFolders the folders that match the text follow after the files. */
export function filterProjectFiles(items, query, accept, withFolders = false) {
  const needle = query.trim().toLocaleLowerCase('en-US');
  const allowed = accept?.map((extension) => extension.toLocaleLowerCase('en-US')) ?? [];
  const named = (item) => !needle || item.name.toLocaleLowerCase('en-US').includes(needle);
  const files = items.filter((item) => !item.isDir && named(item) && (!allowed.length || allowed.some((extension) => item.name.toLocaleLowerCase('en-US').endsWith(extension))));
  return withFolders ? [...files, ...items.filter((item) => item.isDir && named(item))] : files;
}

export function parentFolder(path) {
  const pieces = path.replaceAll('\\', '/').split('/').filter(Boolean);
  pieces.pop();
  return pieces.join('/');
}
