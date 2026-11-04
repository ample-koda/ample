import { build } from 'esbuild';
import { mkdir, stat } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const root = dirname(fileURLToPath(import.meta.url));
const output = join(root, '../web/vendor/codemirror-ample.js');
await mkdir(dirname(output), { recursive: true });
await build({
  entryPoints: [join(root, '../web/codemirror/ample-language.js')],
  outfile: output,
  bundle: true,
  format: 'esm',
  platform: 'browser',
  target: ['es2022'],
  legalComments: 'eof',
  minify: true,
});
console.log(`Built local CodeMirror bundle (${(await stat(output)).size} bytes).`);
