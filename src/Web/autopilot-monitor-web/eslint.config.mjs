// Flat ESLint config (Next 16 removed `next lint`; run via `npm run lint`).
// CI gates on this: errors fail the web job, warnings are ratcheted via
// --max-warnings (see .github/workflows/ci.yml) — lower the cap when you fix
// warnings, never raise it.
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
    // v6 via eslint-config-next 16) flag long-standing patterns whose cleanup
    // is a behavioral refactor per site (auth/SignalR/MSAL-adjacent code), not
    // a mechanical fix. Kept visible as warnings and burned down incrementally
    // under the CI warning ratchet; do NOT silence individual sites with
    // eslint-disable instead of fixing them.
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
