// The songs a revision holds, for "Look only": the song that is open comes first when the revision has it, the others follow in name order.
// Pure, so node --test checks it.
export function peekSongs(files, current) {
  const songs = Object.keys(files ?? {}).filter((path) => /\.amplet$/i.test(path)).sort((a, b) => a.localeCompare(b, 'en'));
  return songs.includes(current) ? [current, ...songs.filter((path) => path !== current)] : songs;
}
