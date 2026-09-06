# Error-code catalog generator

`generate.mjs` rebuilds `src/Shared/AutopilotMonitor.Shared/Resources/error-codes.json`
(schemaVersion 2) from primary sources and refreshes the web copy through
`src/Web/autopilot-monitor-web/scripts/sync-error-codes.js`. The catalog is never edited by
hand: a wrong or missing entry is fixed in the generator (a source, a pin, a family rule) and
regenerated, so the next regeneration cannot lose the fix.

```
IME_ENUMS_JSON=<path> node scripts/error-codes/generate.mjs [--cache-dir DIR] [--check]
```

- `IME_ENUMS_JSON` — enum extract of one Intune Management Extension build (`imeVersion`,
  `customErrorCodes`, `enforcementStates`, `msiRetriableErrorCodes`, `deliveryOptimization`).
  Produced by operator tooling; the generator only reads the JSON.
- `--cache-dir DIR` — keeps the fetched pages as files for offline reruns.
- `--check` — exits 1 when the committed catalog differs from the generator output.

## Entry shape

| Field | Meaning |
|---|---|
| key | lowercase 8-digit hex (`0x80070005`) or a decimal MSI exit code (`1603`). Decimal keys exist only for `0`, `1601`–`1654` and `3010`: exit codes of other installers are author-defined. Win32 codes live only as `0x8007xxxx`; a `HRESULT_FROM_WIN32` value whose low word is in the MSI range resolves to the decimal entry at lookup time (`derivedFromWin32`). |
| `description` | one line, taken from the source (see rules below) |
| `confidence` | `high` = MS Learn table/page or the IME build named in `source`; `medium` = paraphrase of an enum member name or of an analysis rule; `low` = carried over from the v1 catalog without a primary source |
| `source` | `msdoc:<learn.microsoft.com URL>` · `ime:<version>` · `winerror.h` · `rule:<RULE-ID>` · `legacy-catalog-v1` |
| `category` | `msi` `win32` `com` `appx` `windows-update` `cbs` `intune-win32` `intune-mobile` `mdm-enrollment` `autopilot` `entra-join` `delivery-optimization` |
| `symbol` | optional: the `winerror.h`/WU/AppX symbol, or the IME enum member name |
| `imeRetriesDuringEsp` | optional: one of the MSI return codes the IME retries automatically during the Autopilot ESP |

`enforcementStates` is a second map: IME app enforcement state number → `{ name, description }`.

## Sources and family rules

Families are processed in this order; the first family that yields a key wins and later
duplicates are reported on stderr.

| Family | Source | Rule |
|---|---|---|
| win32 | [System Error Codes](https://learn.microsoft.com/en-us/windows/win32/debug/system-error-codes) (per-range pages), [WinHTTP error messages](https://learn.microsoft.com/en-us/windows/win32/winhttp/error-messages) | Seed = codes seen in the field (`WIN32_FIELD_SEEN`) ∪ the IME MSI-retry list ∪ every `0x8007xxxx` code another family names, minus the MSI decimal range and the AppX page's own range. Symbol and text verbatim. A seed code the reference does not list fails the run. |
| msi | [Windows Installer error codes](https://learn.microsoft.com/en-us/windows/win32/msi/error-codes) | Rows in the MSI decimal range only; verbatim. |
| com | `winerror.h` | Hand-listed HRESULTs that are not Win32-derived (`S_OK`, `E_FAIL`, `E_UNEXPECTED`, HTTP/RPC status HRESULTs the Delivery Optimization client names). |
| appx | [Troubleshooting packaging, deployment, and query of Windows apps](https://learn.microsoft.com/en-us/windows/win32/appxpkg/troubleshooting) | Whole table; first sentence of the cell. Authority for its own `0x80073Cxx`/`0x80073Dxx` range. |
| windows-update | [Windows Update error codes by component](https://learn.microsoft.com/en-us/windows/deployment/update/windows-update-error-reference) | Sections an enrollment can meet (agent core, handlers, download manager, protocol talker, data store, drivers, MSI handler, agent setup, AU client); verbatim. |
| windows-update / cbs | [Windows Update common errors](https://learn.microsoft.com/en-us/windows/deployment/update/windows-update-errors) | One table per single-code heading; symbol = text before `;` in the Message cell, description = text after it, else the Description cell. `0x800F…` → `cbs`. |
| intune-mobile / intune-win32 | [Intune app installation error codes](https://learn.microsoft.com/en-us/troubleshoot/mem/intune/app-management/app-install-error-codes) | iOS/iPadOS and Windows tables (Android skipped); description = the message cell. |
| mdm-enrollment / autopilot / entra-join | [Windows enrollment errors](https://learn.microsoft.com/en-us/troubleshoot/mem/intune/device-enrollment/troubleshoot-windows-enrollment-errors), [Autopilot known issues](https://learn.microsoft.com/en-us/autopilot/known-issues) | Prose pages: one-sentence paraphrase listed in `PROSE_ENTRIES`; the generator asserts the code appears on the page. Codes only our analysis rules name carry `rule:` and `medium`. |
| intune-win32 (`0x87D3xxxx`) | IME `CustomErrorCodes` enum | Member name as symbol, humanised as description, `medium`. Two known sign/digit defects in the enum source are corrected by `IME_ENUM_FIXUPS`; a build that fixes them makes the run fail so the fixup is removed. |
| delivery-optimization (`0x80D0xxxx`) | IME `DeliveryOptimizationConstants` | Member name as symbol, humanised, `medium`. |
| legacy | v1 catalog | Entries no primary source covers, `low`. |

Description rules: whole sentences up to 200 characters, cross-reference sentences
("For more information, see …") dropped, no trailing period. `DESCRIPTION_PINS` holds the
few wordings consumers depend on (each says the same as the source row it replaces).

## Regeneration checklist

1. Run the generator (with `--cache-dir` when iterating).
2. Read the stderr summary: entry count per family, dropped duplicates.
3. `git diff` the catalog; a removed key needs a reason (the C#, web and MCP tests pin the
   keys the product relies on).
4. Spot-check ten random entries per family against the source page.
5. Build and run the Backend, Web and MCP test suites.
