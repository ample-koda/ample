// The VS Code extension: starts "ample lsp" and talks to it over stdio. All intelligence is in the server; this file only
// starts it, restarts it and offers a few commands. (Plain JavaScript on purpose: nothing to compile.)
'use strict';
const vscode = require('vscode');
const { LanguageClient, TransportKind } = require('vscode-languageclient/node');

/** @type {LanguageClient | undefined} */
let client;

function start() {
  const cfg = vscode.workspace.getConfiguration('ample');
  const command = cfg.get('server.command', 'ample');
  const args = cfg.get('server.args', ['lsp']);
  const helpBase = cfg.get('help.base', '');
  client = new LanguageClient(
    'ample',
    'AMPLE language server',
    { command, args, transport: TransportKind.stdio },
    { documentSelector: [{ language: 'amplet' }], initializationOptions: helpBase ? { helpBase } : {} },
  );
  return client.start();
}

function terminal(name, line) {
  const t = vscode.window.createTerminal(name);
  t.show();
  t.sendText(line);
}

async function activate(context) {
  await start();
  const cfg = () => vscode.workspace.getConfiguration('ample');
  const exe = () => cfg().get('server.command', 'ample');
  context.subscriptions.push(
    vscode.commands.registerCommand('ample.build', () => {
      const doc = vscode.window.activeTextEditor && vscode.window.activeTextEditor.document;
      terminal('Ample build', `${exe()} build "${doc ? doc.fileName : '.'}"`);
    }),
    vscode.commands.registerCommand('ample.openHelp', () => terminal('Ample help', `${exe()} serve --no-browser`)),
    vscode.commands.registerCommand('ample.showHistory', () => terminal('Ample history', `${exe()} history log`)),
    vscode.commands.registerCommand('ample.restartServer', async () => {
      if (client) await client.stop();
      await start();
    }),
  );
}

function deactivate() {
  return client ? client.stop() : undefined;
}

module.exports = { activate, deactivate };
