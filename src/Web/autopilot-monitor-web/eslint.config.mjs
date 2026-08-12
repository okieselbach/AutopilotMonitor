// Flat ESLint config (Next 16 removed `next lint`; run via `npm run lint`).
// CI gates on this: errors fail the web job, and the codebase is warning-clean —
// CI runs with --max-warnings 0 (see .github/workflows/ci.yml), so any new
// warning fails the build. Fix findings; never raise the cap.
import nextVitals from "eslint-config-next/core-web-vitals";
import nextTs from "eslint-config-next/typescript";

const config = [
  {
    ignores: [
      ".next/**",
      "out/**",
      "build/**",
      "next-env.d.ts",
      "node_modules/**",
      "utils/page-lastmod.generated.ts",
    ],
  },
  ...nextVitals,
  ...nextTs,
  {
    // The React-Compiler-powered hooks rules (new in eslint-plugin-react-hooks
    // v6 via eslint-config-next 16) are kept at "warn" so local dev output stays
    // readable, but the legacy backlog is fully burned down and CI's
    // --max-warnings 0 fails the build on any new finding. Fix new sites with
    // the established patterns (inner-async wrapper for fetch effects,
    // derive/adjust-during-render for state sync, useSyncExternalStore for
    // browser APIs); do NOT silence sites with eslint-disable.
    rules: {
      "react-hooks/set-state-in-effect": "warn",
      "react-hooks/refs": "warn",
      "react-hooks/static-components": "warn",
      "react-hooks/purity": "warn",
      "react-hooks/immutability": "warn",
      "react-hooks/preserve-manual-memoization": "warn",
    },
  },
  {
    // Build/maintenance scripts are Node CommonJS on purpose (the package is
    // not type:module) — require() is the correct import form there.
    files: ["scripts/**/*.js"],
    rules: {
      "@typescript-eslint/no-require-imports": "off",
    },
  },
];

export default config;
