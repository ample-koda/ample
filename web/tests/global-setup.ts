// Starts the shared "ample serve" for the specs that do not change history or seeds, and tells them where it is.
// One server for each worker: openApp rewrites the song of the server it talks to, so two workers on one server rewrote each other's song in the middle of a test
// (the cause of most flaky failures of a run with --workers=2). A worker picks its server by TEST_PARALLEL_INDEX (see launchUrl in support.ts).
import type { FullConfig } from '@playwright/test';
import { demoSong, startServer } from './server';

export default async function globalSetup(config: FullConfig) {
  const servers = [];
  for (let i = 0; i < Math.max(1, config.workers || 1); i++) servers.push(await startServer());
  process.env.AMPLE_LAUNCH_URL = servers[0].launch;
  process.env.AMPLE_LAUNCH_URLS = JSON.stringify(servers.map((s) => s.launch));
  process.env.AMPLE_DEMO_SONG = demoSong();
  return async () => { for (const s of servers) await s.stop(); };
}
