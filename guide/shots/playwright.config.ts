import { defineConfig } from '@playwright/test';

// Only for the screenshots of the guide (npm run guide:shots). It is not part of the test suite.
export default defineConfig({
  testDir: '.',
  testMatch: 'guide-shots*.spec.ts',
  workers: 1,
  timeout: 120000,
  reporter: [['list']],
  use: { trace: 'off' },
  projects: [{ name: 'chromium', use: { browserName: 'chromium' } }],
});
