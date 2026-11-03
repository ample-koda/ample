// A stand-in for ace-server, for tests and for trying the plugin without a GPU. It implements POST /lm, POST /synth, GET /props and GET /job.
// Usage: node stub-server.mjs [mode]; modes: ok, nobase, error500, malformed, neverdone, noaudio, jobfailed. Prints "PORT <n>" when it listens.
import http from 'node:http';

const mode = process.argv[2] || 'ok';
const requests = [];
const jobs = new Map();

function audio(request) {
  const rate = 24000, seconds = Math.max(0.05, Math.min(request.duration || 1, 30)), n = Math.floor(rate * seconds), buf = Buffer.alloc(44 + n * 4);
  buf.write('RIFF', 0); buf.writeInt32LE(36 + n * 4, 4); buf.write('WAVEfmt ', 8); buf.writeInt32LE(16, 16); buf.writeInt16LE(1, 20); buf.writeInt16LE(2, 22);
  buf.writeInt32LE(rate, 24); buf.writeInt32LE(rate * 4, 28); buf.writeInt16LE(4, 32); buf.writeInt16LE(16, 34); buf.write('data', 36); buf.writeInt32LE(n * 4, 40);
  const inc = 150 + (request.seed % 500);
  for (let i = 0; i < n; i++) { const v = Math.round(Math.sin((2 * Math.PI * inc * i) / rate) * 8000); buf.writeInt16LE(v, 44 + i * 4); buf.writeInt16LE(Math.round(v / 2), 46 + i * 4); }
  return buf;
}

const server = http.createServer((req, res) => {
  const send = (code, body, type = 'application/json') => { res.writeHead(code, { 'content-type': type }); res.end(body); };
  if (mode === 'error500') { return send(500, JSON.stringify({ error: 'boom' })); }
  if (req.method === 'GET' && req.url === '/props') { return send(200, JSON.stringify({ models: { lm: ['acestep-5Hz-lm-4B-Q8_0.gguf'], dit: mode === 'nobase' ? ['acestep-v15-turbo-Q8_0.gguf'] : ['acestep-v15-base-Q8_0.gguf', 'acestep-v15-turbo-Q8_0.gguf', 'acestep-v15-sft-Q5_K_M.gguf'] } })); }
  if (req.method === 'POST' && (req.url === '/lm' || req.url === '/synth')) {
    const chunks = [];
    req.on('data', c => { chunks.push(c); });
    req.on('end', () => {
      if (mode === 'rejectmodel' && req.url === '/synth') { return send(400, JSON.stringify({ error: 'synthetic BF16 rejection detail' })); }
      if (mode === 'malformed') { return send(200, 'this is not json'); }
      const body = Buffer.concat(chunks).toString('utf8');
      const requestJson = req.headers['content-type']?.startsWith('multipart/form-data')
        ? body.match(/name="request"[^\r\n]*\r\nContent-Type:[^\r\n]*\r\n\r\n([\s\S]*?)\r\n--/)?.[1]
        : body;
      if (!requestJson) { return send(400, JSON.stringify({ error: 'missing request' })); }
      const request = JSON.parse(requestJson); requests.push(request);
      const id = 'j' + requests.length; jobs.set(id, { count: 0, kind: req.url.slice(1), request });
      send(202, JSON.stringify({ id }));
    });
    return undefined;
  }
  if (req.method === 'GET' && req.url === '/requests') { return send(200, JSON.stringify(requests)); }
  const url = new URL(req.url, 'http://127.0.0.1');
  if (req.method === 'GET' && url.pathname === '/job') {
    const job = jobs.get(url.searchParams.get('id'));
    if (!job) { return send(404, '{}'); }
    if (url.searchParams.get('result') === '1') {
      if (job.kind === 'lm') { return send(200, JSON.stringify([job.request])); }
      const wav = mode === 'noaudio' ? Buffer.from('--ace-batch-boundary\r\nContent-Type: application/json\r\n\r\n{}\r\n--ace-batch-boundary--\r\n') : Buffer.concat([Buffer.from('--ace-batch-boundary\r\nContent-Type: audio/wav\r\n\r\n'), audio(job.request), Buffer.from('\r\n--ace-batch-boundary--\r\n')]);
      return send(200, wav, 'multipart/mixed; boundary=ace-batch-boundary');
    }

    job.count++;
    if (mode === 'jobfailed') { return send(200, JSON.stringify({ status: 'failed', error: 'synthetic ACE worker failure detail' })); }
    return send(200, JSON.stringify(mode === 'neverdone' || job.count < 3 ? { status: 'running', progress: 0.3 } : { status: 'done' }));
  }
  return send(404, '{}');
});
server.listen(0, '127.0.0.1', () => { console.log('PORT ' + server.address().port); });
