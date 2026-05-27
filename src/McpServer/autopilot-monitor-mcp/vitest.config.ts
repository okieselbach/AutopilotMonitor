import { defineConfig } from 'vitest/config';

export default defineConfig({
  test: {
    // Only run TypeScript sources. Without this, vitest's default glob also
    // matches the compiled copies under dist/ (gitignored build output), so a
    // stale `npm run build` would double-run every suite — and a since-fixed
    // test could "fail" from its outdated dist twin.
    include: ['src/**/*.test.ts'],
    testTimeout: 30_000,
    hookTimeout: 10_000,
  },
});
