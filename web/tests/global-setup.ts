// Starts the shared "ample serve" for the specs that do not change history or seeds, and tells them where it is.
import { demoSong, startServer } from './server';

export default async function globalSetup() {
  const server = await startServer();
  process.env.AMPLE_LAUNCH_URL = server.launch;
  process.env.AMPLE_DEMO_SONG = demoSong();
  return server.stop;
}
