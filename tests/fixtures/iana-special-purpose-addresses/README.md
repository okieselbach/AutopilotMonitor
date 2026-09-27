# IANA Special-Purpose Address Registries — verbatim

Unedited CSV exports of the two IANA registries, retrieved 2026-09-27:

- `iana-ipv4-special-registry-1.csv` — https://www.iana.org/assignments/iana-ipv4-special-registry/iana-ipv4-special-registry-1.csv
- `iana-ipv6-special-registry-1.csv` — https://www.iana.org/assignments/iana-ipv6-special-registry/iana-ipv6-special-registry-1.csv

Both SSRF address classifiers are tested against every block listed here, independently of
their own range tables:

- MCP server — `isPublicAddress` in `src/McpServer/autopilot-monitor-mcp/src/cimd.ts`
  (test: `src/__tests__/cimd.test.ts`)
- Backend — `SsrfGuard.IsBlockedAddress` in `src/Backend/AutopilotMonitor.Functions/Security/SsrfGuard.cs`
  (test: `SsrfGuardTests.cs`)

To update, replace both files with a fresh download, keep them unedited, and update the date
above. The test readers handle the registry's quoting, multi-line cells, footnote markers
such as `2002::/16 [3]` and cells that list two blocks.
