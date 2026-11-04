const assert = require('node:assert/strict');
const path = require('node:path');
const vscode = require('vscode');

exports.run = async () => {
  const extension = vscode.extensions.getExtension('ample.ample-vscode');
  assert.ok(extension, 'The installed AMPLE VSIX must be loaded');
  process.stdout.write('AMPLE VS Code extension loaded\n');
  const file = path.join(vscode.workspace.workspaceFolders[0].uri.fsPath, 'smoke.amplet');
  const document = await vscode.workspace.openTextDocument(vscode.Uri.file(file));
  assert.equal(document.languageId, 'amplet', 'The .amplet language contribution must be active');
  await vscode.window.showTextDocument(document);
  process.stdout.write('AMPLE VS Code test document opened\n');
  const config = vscode.workspace.getConfiguration('ample');
  const command = config.get('server.command');
  const args = config.get('server.args');
  assert.match(path.basename(command).toLowerCase(), /node(\.exe)?$/, 'The isolated test workspace must configure its local Node LSP server');
  assert.ok(args[0].endsWith(path.join('test', 'fixtures', 'lsp-server.cjs')));
  try {
    await Promise.race([
      extension.activate(),
      new Promise((_, reject) => setTimeout(() => reject(new Error('AMPLE language server activation timed out')), 15000)),
    ]);
  } catch (error) {
    process.stderr.write(`AMPLE VS Code activation failed: ${error.stack || error}\n`);
    throw error;
  }
  process.stdout.write('AMPLE VS Code extension activated\n');

  const commands = await vscode.commands.getCommands(true);
  assert.ok(commands.includes('ample.build'), 'The build command must be registered');
  const completions = await vscode.commands.executeCommand('vscode.executeCompletionItemProvider', document.uri, new vscode.Position(1, 0));
  assert.ok(completions.items.some(item => item.label === 'AMPLE integration'), 'The installed language client must return an LSP completion');
  process.stdout.write('AMPLE VS Code language-server completion passed\n');
  await vscode.commands.executeCommand('workbench.action.closeActiveEditor');
};
