import * as fs from 'node:fs';
import * as path from 'node:path';
import { fileURLToPath } from 'node:url';
import { test, expect } from '@playwright/test';
import { api, freshServer, ownServer } from '../../web/tests/support';

// A package must carry the component files the song imports (and the files they import): it has to build without the project folder.
const DEMO = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../demo-projects/components/components');

freshServer({
  song: 'composition "export" { ample 1  tempo 100  master-seed 1 }\nimport "components/wet-dry.amplec"\nmidi-clip melody at 1 for 2 {\n  notes 1:0:1:60:100 1:1:1:64:100\n}\npiano lead at 1 for 2 {\n  from melody.notes -> notes\n}\nkoda.fx.wetDry blend {\n  from lead.audio -> audio\n}\nmaster out {\n  from blend.audio -> audio\n}\n',
  prepare: (_cli, folder) => {
    fs.mkdirSync(path.join(folder, 'components'), { recursive: true });
    fs.copyFileSync(path.join(DEMO, 'wet-dry.amplec'), path.join(folder, 'components', 'wet-dry.amplec'));
  },
});

test('a Full package holds the imported component file', async () => {
  await api('/api/doc/save', { method: 'POST', body: { path: 'song.amplet' } });
  const made = await api('/api/tx/export', { method: 'POST', body: { path: 'song.amplet', mode: 'full', name: 'with-component' } });
  expect(JSON.stringify(made).slice(0, 600)).not.toMatch(/AMP5|error/);
  const file = path.join(ownServer()!.folder, 'exports', 'with-component.ampletx');
  expect(fs.readFileSync(file, 'utf8')).toContain('components/wet-dry.amplec');
});
