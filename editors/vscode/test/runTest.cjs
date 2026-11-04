const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawnSync } = require('node:child_process');
const { createVSIX } = require('@vscode/vsce');
const { resolveCliArgsFromVSCodeExecutablePath, runTests } = require('@vscode/test-electron');

async function main() {
  const extensionRoot = path.resolve(__dirname, '..');
  const manifest = require(path.join(extensionRoot, 'package.json'));
  const executable = process.env.AMPLE_VSCODE_EXECUTABLE;
  if (!executable || !fs.existsSync(executable)) { throw new Error('Set AMPLE_VSCODE_EXECUTABLE to an installed VS Code 1.85+ executable; the test never downloads software'); }
  const scratch = fs.mkdtempSync(path.join(os.tmpdir(), 'ample-vscode-test-'));
  const workspace = path.join(scratch, 'workspace');
  const extensions = path.join(scratch, 'extensions');
  const userData = path.join(scratch, 'user-data');
  const vsix = path.join(scratch, 'ample.vsix');
  let completed = false;

  try {
    fs.mkdirSync(path.join(workspace, '.vscode'), { recursive: true });
    fs.writeFileSync(path.join(workspace, 'smoke.amplet'), 'project "smoke" { tempo 120 master-seed 1 }\n\n');
    fs.writeFileSync(path.join(workspace, '.vscode', 'settings.json'), JSON.stringify({
      'ample.server.command': process.execPath,
      'ample.server.args': [path.join(extensionRoot, 'test', 'fixtures', 'lsp-server.cjs')],
      'editor.experimental.asyncTokenization': false,
    }));

    await createVSIX({ cwd: extensionRoot, packagePath: vsix, allowMissingRepository: true, skipLicense: true });
    const [cli, ...cliArgs] = resolveCliArgsFromVSCodeExecutablePath(executable);
    const install = spawnSync(cli, [...cliArgs, '--install-extension', vsix, '--extensions-dir', extensions, '--user-data-dir', userData], { encoding: 'utf8', windowsHide: true, shell: process.platform === 'win32' });
    if (install.status !== 0) { throw new Error(`VS Code could not install the AMPLE VSIX (exit ${install.status ?? install.error?.message})`); }

    const extensionFolder = path.join(extensions, `${manifest.publisher}.${manifest.name}-${manifest.version}`);
    if (!fs.existsSync(path.join(extensionFolder, 'package.json'))) { throw new Error('Installed AMPLE extension folder was not created'); }

    const result = await runTests({
      vscodeExecutablePath: executable,
      extensionDevelopmentPath: extensionFolder,
      extensionTestsPath: path.join(extensionRoot, 'test', 'suite', 'index.js'),
      launchArgs: [workspace, `--extensions-dir=${extensions}`, `--user-data-dir=${userData}`, '--disable-extensions'],
    });
    if (result !== 0) { throw new Error(`VS Code extension integration tests failed (${result})`); }
    completed = true;
  } finally {
    if (completed) { fs.rmSync(scratch, { recursive: true, force: true }); }
    else { process.stderr.write(`Isolated VS Code test files retained at ${scratch}\n`); }
  }
}

main().catch(error => {
  process.stderr.write(`${error.stack || error.message}\n`);
  process.exitCode = 1;
});
