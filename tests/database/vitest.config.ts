import { defineConfig } from 'vitest/config';

export default defineConfig({
  test: {
    include: ['test/**/*.test.ts'],
    globalSetup: ['./src/global-setup.ts'],
    // Every file creates, migrates and drops its own database.
    testTimeout: 30_000,
    hookTimeout: 60_000,
  },
});
