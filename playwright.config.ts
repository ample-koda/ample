import { defineConfig } from '@playwright/test';

export default defineConfig({
  testDir: 'spec-tests/parity',
  testMatch: '*.spec.ts',
  globalSetup: './web/tests/global-setup.ts',
  workers: 1,
  timeout: 45000,
  expect: { timeout: 10000 },
  reporter: [['list']],
  use: { viewport: { width: 1400, height: 900 }, trace: 'off' },
  projects: [
    { name: 'chromium', testIgnore: '**/*.phone.spec.ts', grepInvert: /@slow/, use: { browserName: 'chromium' } },
    { name: 'slow', testIgnore: '**/*.phone.spec.ts', grep: /@slow/, use: { browserName: 'chromium' } },
    // phone viewport with touch (mobile-ui plan): npx playwright test --project=phone
    { name: 'phone', testMatch: '*.phone.spec.ts', use: { browserName: 'chromium', viewport: { width: 390, height: 844 }, hasTouch: true, isMobile: true, deviceScaleFactor: 2 } },
  ],
});
