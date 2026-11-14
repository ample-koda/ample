// A guard for the Start menu entries of the MSI (Package.wxs cannot be installed outside Windows Installer):
// the studio entry must not belong only to the optional tray hub (issue 303). The source is read as text.
// Run: node --test install/msi/studio-shortcut.test.mjs
import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const wxs = readFileSync(fileURLToPath(new URL('./Package.wxs', import.meta.url)), 'utf8').replace(/\r\n/g, '\n');

/** The text of a <Feature Id="..."> element up to its own closing tag (features may nest, so the depth is followed). */
function featureText(id) {
  const start = wxs.indexOf(`<Feature Id="${id}"`);
  assert.ok(start >= 0, `feature ${id} exists`);
  let depth = 0;
  const tags = /<Feature\b|<\/Feature>/g;
  tags.lastIndex = start;
  for (let m = tags.exec(wxs); m; m = tags.exec(wxs)) {
    depth += m[0] === '</Feature>' ? -1 : 1;
    if (depth === 0) return wxs.slice(start, m.index + m[0].length);
  }
  throw new Error(`feature ${id} is not closed`);
}

test('the mandatory program feature has a studio entry that does not need the tray hub', () => {
  const program = featureText('Program');
  assert.match(program, /<Shortcut Id="StudioCliShortcut"[^>]*Target="\[INSTALLFOLDER\]ample\.exe"[^>]*Arguments="open --last"/);
  assert.doesNotMatch(program.match(/<Shortcut Id="StudioCliShortcut"[^>]*>/)[0], /Ample\.Hub\.exe/);
});

test('the program feature cannot be left out, so the studio entry is always possible', () => {
  assert.match(featureText('Program').split('\n')[0], /AllowAbsent="no"/);
});

test('with the tray hub installed the hub entry is the one that is shown, so the two never share a name', () => {
  const hub = featureText('Hub');
  assert.match(hub, /<Shortcut Id="StudioShortcut" Name="AMPLE"[^>]*Ample\.Hub\.exe/);
  const cli = wxs.match(/<Shortcut Id="StudioCliShortcut"[^>]*>/)[0];
  assert.doesNotMatch(cli, /Name="AMPLE"(\s|\/|>)/);
});

test('the fallback entry is only installed when the hub is not (a build with the hub says so in its condition)', () => {
  assert.match(wxs, /<Component Id="StudioCliShortcutComponent"[^>]*Condition="NOT &amp;Hub=3"/);
});

test('the fallback component is a normal per-user component: a registry key path and a folder removal on uninstall', () => {
  const block = wxs.slice(wxs.indexOf('<Component Id="StudioCliShortcutComponent"'));
  const component = block.slice(0, block.indexOf('</Component>'));
  assert.match(component, /<RegistryValue Root="HKCU" Key="Software\\AMPLE\\msi" Name="menu-studio-cli"[^>]*KeyPath="yes"/);
  assert.match(component, /<RemoveFolder Id="RemoveAmpleMenuFolder3" On="uninstall"/);
});

test('every component, shortcut and folder removal id is used once per build variant', () => {
  const hubBuild = wxs.replace(/<\?else\?>[\s\S]*?<\?endif\?>/g, '').replace(/<\?(if|endif)[^?]*\?>/g, '');
  for (const attribute of ['Component Id', 'Shortcut Id', 'RemoveFolder Id']) {
    const ids = [...hubBuild.matchAll(new RegExp(`<${attribute}="([^"]+)"`, 'g'))].map((m) => m[1]);
    assert.equal(new Set(ids).size, ids.length, `${attribute} values are unique: ${ids.join(', ')}`);
  }
});
