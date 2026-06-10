import { defineConfig, devices } from '@playwright/test';

/**
 * Mobile-rendering smoke for the survey page. By default runs against
 * BOTH dev + prod URLs across three devices (iPhone 13, Pixel 5, iPhone SE
 * — the small viewport that exposed the hero-clip bug on 2026-06-10).
 *
 * Override the env target with TARGET=DEV or TARGET=PROD to run against
 * just one environment.
 */
export default defineConfig({
  testDir: '.',
  timeout: 30_000,
  expect: { timeout: 5_000 },
  reporter: [
    ['html', { open: 'never', outputFolder: 'playwright-report' }],
    ['list'],
  ],
  use: {
    actionTimeout: 10_000,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    video: 'retain-on-failure',
    ignoreHTTPSErrors: false,
  },
  projects: [
    {
      name: 'iPhone 13',
      use: { ...devices['iPhone 13'] },
    },
    {
      name: 'Pixel 5',
      use: { ...devices['Pixel 5'] },
    },
    {
      name: 'iPhone SE (narrow viewport)',
      // iPhone SE is 375x667 -- the small viewport that exposed the
      // hero-band clip on the original min-height + flex centring.
      use: { ...devices['iPhone SE'] ?? devices['iPhone 12 mini'] },
    },
  ],
});
