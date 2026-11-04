let buffer = Buffer.alloc(0);

process.stdin.on('data', chunk => {
  buffer = Buffer.concat([buffer, chunk]);
  for (;;) {
    const boundary = buffer.indexOf('\r\n\r\n');
    if (boundary < 0) { return; }
    const header = buffer.subarray(0, boundary).toString('ascii');
    const length = Number(header.match(/Content-Length: (\d+)/i)?.[1]);
    if (!Number.isSafeInteger(length) || length < 0 || buffer.length < boundary + 4 + length) { return; }
    const message = JSON.parse(buffer.subarray(boundary + 4, boundary + 4 + length).toString('utf8'));
    buffer = buffer.subarray(boundary + 4 + length);
    respond(message);
  }
});

function respond(message) {
  if (message.method === 'exit') { process.exitCode = 0; return; }
  if (message.id === undefined) { return; }
  const result = message.method === 'initialize'
    ? { capabilities: { textDocumentSync: 1, completionProvider: {} } }
    : message.method === 'textDocument/completion'
      ? { isIncomplete: false, items: [{ label: 'AMPLE integration', kind: 14 }] }
      : null;
  const body = Buffer.from(JSON.stringify({ jsonrpc: '2.0', id: message.id, result }), 'utf8');
  process.stdout.write(Buffer.concat([Buffer.from(`Content-Length: ${body.length}\r\n\r\n`, 'ascii'), body]));
}
