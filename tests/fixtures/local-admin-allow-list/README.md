# Local Admin allowed-list entries — shared cases

The Local Admin Analyzer's allowed list (tenant setting **Allowed Local Accounts**) is a list of entries:
an account name or a glob (`*` any run, `?` one character), optionally followed by `|user` — the account
is then allowed only while it is not a member of the local Administrators group. `cases.json` is the one
definition of that grammar, run by every implementation:

- C# — `LocalAdminAllowList` in `src/Shared/AutopilotMonitor.Shared/Models/Config/LocalAdminAllowList.cs`
  (backend test: `LocalAdminAllowListParityTests.cs`; agent test, which runs the netstandard2.0 build the agent
  ships: `LocalAdminAllowListFixtureTests.cs`)
- Web — `app/settings/lib/allowedAccounts.ts` in `src/Web/autopilot-monitor-web`
  (test: `app/settings/lib/__tests__/allowedAccounts.test.ts`)

`parse` is what the agent reads: an entry without `|` is the pattern verbatim (role `any`) — exactly what it
meant before qualifiers existed; with `|`, only `pattern|user` parses (qualifier case-insensitive, no spaces
around the separator). Everything else is `ignored`: the agent logs it and it allows nothing.

`write` is what a save accepts for an entry that is NEW in the list (stored entries are never re-checked):
at most 64 characters, no control characters, no leading or trailing space, it must parse, no character
Windows rejects in account names (`" / \ [ ] : ; = , + < >`), no SID (`S-1-<digit>…`), and a glob needs at
least two characters besides `*` and `?`.

A change to the grammar goes into `cases.json` first; the tests then say which side is behind.
