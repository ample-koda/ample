// The rules of check-security-patterns.mjs catch the patterns that caused findings and leave the safe forms alone.
import test from 'node:test';
import assert from 'node:assert/strict';
import { RULES } from './check-security-patterns.mjs';

const rule = (id) => RULES.find((r) => r.id === id);
const hit = (id, text) => rule(id).pattern.test(text);

test('a project path resolved without the file system is caught, with it is not', () => {
  assert.ok(hit('path-without-link-check', 'string full = ProjectPath.Resolve(s.Root, rel);'));
  assert.ok(hit('path-without-link-check', 'string full = ProjectPath.Resolve(context.ProjectRoot, relativePath);'));
  assert.ok(!hit('path-without-link-check', 'string full = ProjectPath.Resolve(s.Fs, s.Root, rel);'));
  assert.ok(!hit('path-without-link-check', 'string full = ProjectPath.Resolve(task.ProjectDir, rel);'));
});

test('the private folder tested by the text of the path is caught', () => {
  assert.ok(hit('private-folder-by-text', 'if (rel.StartsWith(".ample", StringComparison.Ordinal)) return;'));
  assert.ok(hit('private-folder-by-text', 'if (rel.StartsWith(ProjectLayout.AmpleFolder, StringComparison.OrdinalIgnoreCase)) return;'));
  assert.ok(!hit('private-folder-by-text', 'if (ProjectPath.IsPrivate(rel)) return;'));
  assert.ok(!hit('private-folder-by-text', 'if (name.StartsWith(".amplet")) return;'));
});

test('zip extraction, cmd.exe, shell execute and a package path joined to the root are caught', () => {
  assert.ok(hit('zip-without-limits', 'ZipFile.ExtractToDirectory(zip, folder);'));
  assert.ok(hit('zip-without-limits', 'entry.ExtractToFile(target, true);'));
  assert.ok(!hit('zip-without-limits', 'SafeZip.ExtractAll(zip, folder, 1L << 30, 20_000);'));
  assert.ok(hit('cmd-exe', 'ProcessStartInfo info = new("cmd.exe") { };'));
  assert.ok(!hit('cmd-exe', 'ProcessStartInfo info = new("ffmpeg") { };'));
  assert.ok(hit('shell-execute', 'Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });'));
  assert.ok(!hit('shell-execute', 'new ProcessStartInfo(exe) { UseShellExecute = false };'));
  assert.ok(hit('path-from-text-combined', 'string target = Path.Combine(targetRoot, item.Destination);'));
});

test('a plugin folder inside a project is caught, the installed folders are not', () => {
  assert.ok(hit('plugin-from-project', 'PluginHost.Discover([Path.Combine(s.AmpleRoot, "plugins"), Path.Combine(s.Root, "plugins")]);'));
  assert.ok(!hit('plugin-from-project', 'PluginHost.Discover([Path.Combine(s.AmpleRoot, "plugins")]);'));
  assert.ok(!hit('plugin-from-project', 'PluginHost.Discover(InstalledPluginFolders(s.AmpleRoot));'));
});

test('an ffmpeg input that may name a network address is caught, a local-only or piped one is not', () => {
  const id = 'ffmpeg-input-without-protocol-whitelist';
  assert.ok(hit(id, 'new[] { "-v", "error", "-y", "-i", path, "-vn" }'));
  assert.ok(!hit(id, 'new[] { "-v", "error", "-y", "-protocol_whitelist", "file", "-i", path, "-vn" }'));
  assert.ok(!hit(id, '["-hide_banner", "-f", "wav", "-i", "pipe:0", "-vn"]'));
});

test('a number of a song read with Parse is caught, TryParse and a parse of a short text are not', () => {
  const id = 'parse-of-song-number-throws';
  assert.ok(hit(id, 'int n = int.Parse(mm.Groups["mn"].Value, CultureInfo.InvariantCulture);'));
  assert.ok(hit(id, 'long whole = long.Parse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture);'));
  assert.ok(hit(id, 'long whole = long.Parse(text.AsSpan(start, Position - start), NumberStyles.None, CultureInfo.InvariantCulture);'));
  assert.ok(!hit(id, 'int n = int.TryParse(mm.Groups["mn"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int tn) ? tn : 0;'));
  assert.ok(!hit(id, 'int times = int.Parse(parts[1], CultureInfo.InvariantCulture);'));
});

test('xml read without a limit on entities is caught, an xml reader with settings is not', () => {
  assert.ok(hit('xml-with-entities', 'XDocument doc = XDocument.Parse(xml);'));
  assert.ok(hit('xml-with-entities', 'XmlDocument document = new XmlDocument();'));
  assert.ok(!hit('xml-with-entities', 'XDocument doc = XDocument.Load(xmlReader);'));
});

test('the session cookie of the page is marked Secure over https and only then', async () => {
  const api = new URL('../web/app/api.js', import.meta.url).href;
  const saved = { location: globalThis.location, document: globalThis.document, history: globalThis.history, sessionStorage: globalThis.sessionStorage };
  try {
    const written = {};
    for (const protocol of ['https:', 'http:']) {
      const store = new Map();
      Object.assign(globalThis, {
        location: { protocol, port: '', hash: '#token=abcdef0123456789abcdef', pathname: '/', search: '' },
        history: { replaceState() {} },
        sessionStorage: { setItem: (k, v) => store.set(k, v), getItem: (k) => store.get(k) ?? null },
        document: { get cookie() { return ''; }, set cookie(v) { if (v.startsWith('ample-token-') && !v.includes('max-age=0')) written[protocol] = v; } },
      });
      (await import(api + '?' + protocol)).initToken();
    }
    assert.match(written['https:'], /; Secure$/);
    assert.doesNotMatch(written['http:'], /Secure/);
  } finally {
    for (const [name, value] of Object.entries(saved)) {
      if (value === undefined) delete globalThis[name]; else globalThis[name] = value;
    }
  }
});

test('every rule says what to do instead and where it applies', () => {
  for (const r of RULES) {
    assert.ok(r.id && r.pattern instanceof RegExp && r.only instanceof RegExp && r.use.length > 10, r.id);
  }
});
