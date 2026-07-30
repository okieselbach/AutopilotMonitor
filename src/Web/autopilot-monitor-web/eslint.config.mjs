// Flat ESLint config (Next 16 removed `next lint`; run via `npm run lint`).
// Not wired into CI — kept runnable for local/spot use only.
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
];

export default config;
