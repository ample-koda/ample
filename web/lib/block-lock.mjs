// Whether a block is locked (it has the lock flag), looked up in the nodes of the song as they are NOW, so a window or a card that was drawn before
// the lock was set cannot write through it. Pure, so node --test checks it.

export function isBlockLocked(nodes, id) {
  const node = (nodes ?? []).find((n) => n?.id === id);
  return Boolean(node?.flags?.includes?.('lock'));
}
