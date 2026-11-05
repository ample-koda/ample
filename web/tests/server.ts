// Starts a real "ample serve" on a temporary project that holds the demo song.
import { spawn, spawnSync, ChildProcess } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const ample = path.resolve(here, '..', '..');

function cliDll(): string {
  if (process.env.AMPLE_CLI_DLL) return process.env.AMPLE_CLI_DLL;
  for (const cfg of ['Debug', 'Release']) {
    const p = path.join(ample, 'src', 'Ample.Cli', 'bin', cfg, 'net9.0', 'Ample.Cli.dll');
    if (fs.existsSync(p)) return p;
  }
  throw new Error('Build the solution first: dotnet build ample/Ample.sln');
}

export const demoSong = () => fs.readFileSync(path.join(here, 'fixtures', 'demo.amplet'), 'utf8');

export interface ServerOptions {
  /** The song instead of the demo song. */
  song?: string;
  /** Extra files of the project (path to text). */
  files?: Record<string, string>;
  /** Run the command line tool on the project before the server starts (for example a simulated crash). */
  prepare?: (cli: (...args: string[]) => void, folder: string) => void;
  /** Extra environment variables of this server only (never set them on process.env of the test run: other files would inherit them). */
  env?: Record<string, string>;
}

export async function startServer(options: ServerOptions = {}): Promise<{ launch: string; folder: string; home: string; stop: () => Promise<void> }> {
  const dll = cliDll();
  const folder = fs.mkdtempSync(path.join(os.tmpdir(), 'ample-web-'));
  const home = `${folder}-home`;
  const made = spawnSync('dotnet', [dll, 'new', folder, '--name', 'demo'], { encoding: 'utf8' });
  if (made.status !== 0) throw new Error(`ample new failed (${made.error?.message ?? `exit ${made.status}`}): ${made.stdout}${made.stderr}`);
  fs.rmSync(path.join(folder, 'demo.amplet'), { force: true });   // `ample new` gives every project the demo song; these specs bring their own song
  fs.writeFileSync(path.join(folder, 'song.amplet'), options.song ?? demoSong());
  for (const [rel, text] of Object.entries(options.files ?? {})) {
    fs.mkdirSync(path.dirname(path.join(folder, rel)), { recursive: true });
    fs.writeFileSync(path.join(folder, rel), text);
  }

  if (options.prepare) options.prepare((...args) => { const r = spawnSync('dotnet', [dll, ...args], { encoding: 'utf8', env: { ...process.env, ...options.env, AMPLE_HOME: home } }); if (r.status !== 0) throw new Error(`ample ${args.join(' ')} failed: ${r.stdout}${r.stderr}`); }, folder);
  const child: ChildProcess = spawn('dotnet', [dll, 'serve', '--project', folder, '--root', ample, '--no-browser'], { stdio: ['ignore', 'pipe', 'pipe'], env: { ...process.env, ...options.env, AMPLE_HOME: home } });
  const launch: string = await new Promise((resolve, reject) => {
    let out = '';
    const timer = setTimeout(() => reject(new Error(`ample serve did not start: ${out}`)), 30000);
    child.stdout!.on('data', (d) => {
      out += d.toString();
      const m = /Open: (\S+)/.exec(out);
      if (m) { clearTimeout(timer); resolve(m[1]); }
    });
    child.on('exit', (code) => reject(new Error(`ample serve exited with ${code}: ${out}`)));
  });
  return {
    launch,
    folder,
    home,
    stop: async () => {
      child.kill();
      await new Promise((r) => setTimeout(r, 300));
      for (const f of [folder, home]) { try { fs.rmSync(f, { recursive: true, force: true }); } catch { /* the folder may still be locked on Windows */ } }
    },
  };
}
