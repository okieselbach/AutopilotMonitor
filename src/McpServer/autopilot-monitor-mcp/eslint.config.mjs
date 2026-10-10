// Flat ESLint config, run via `npm run lint`. CI runs it with --max-warnings 0
// (see .github/workflows/ci.yml), so any new warning fails the mcp job.
// Fix findings; never raise the cap.
import js from '@eslint/js';
import tseslint from 'typescript-eslint';

const config = [
  {
    ignores: ['dist/**', 'node_modules/**', 'src/generated/**', 'src/*.generated.ts'],
  },
  js.configs.recommended,
  ...tseslint.configs.recommended,
  {
    rules: {
      '@typescript-eslint/no-unused-vars': [
        'error',
        { argsIgnorePattern: '^_', varsIgnorePattern: '^_', ignoreRestSiblings: true },
      ],
    },
  },
  {
    // An unhandled rejection ends the server process, so the promise rules run with type information.
    files: ['src/**/*.ts', 'scripts/**/*.ts', 'vitest.config.ts'],
    languageOptions: {
      parserOptions: { projectService: true, tsconfigRootDir: import.meta.dirname },
    },
    rules: {
      '@typescript-eslint/no-floating-promises': 'error',
      '@typescript-eslint/no-misused-promises': 'error',
    },
  },
  {
    // Tests cast mocks and untyped backend JSON (the live tools-* suites); `any` stays allowed there.
    files: ['src/**/*.test.ts', 'src/__tests__/**/*.ts'],
    rules: {
      '@typescript-eslint/no-explicit-any': 'off',
    },
  },
];

export default config;
