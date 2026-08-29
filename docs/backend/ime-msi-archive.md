---
type: Concept
title: IME Installer Archive (version-verified, multi-host)
description: How every fleet-observed Intune Management Extension build is archived as its MSI - the ime_agent_version sighting that queues the job, the candidate hosts, the ProductVersion check that keeps a wrong ring's build out of the version folder, the re-queue on later sightings, and the manual skill-side backfill.
resource: src/Backend/AutopilotMonitor.Functions/Services/Ime/ImeMsiArchiver.cs
tags:
  - backend
  - ime
  - archive
  - blob
  - queue
timestamp: 2026-08-29T17:30:00+02:00
---

# IME Installer Archive

The platform keeps every Intune Management Extension build it has ever seen in the fleet
as the original installer, so any IME version can be decompiled and diffed later
(`ime-decompiles` repository, `/ime-decompile` skill) even after Microsoft's CDN has moved
on. Two stores:

| Store | Content | Written by |
| --- | --- | --- |
| Blob container `ime-archive` (private, kept by design, not tenant-scoped) | `{version}/IntuneWindowsAgent.msi` (write-once) + `{version}/provenance.json` (derived, overwritable) | `ImeMsiArchiver` via the `ime-msi-archive` queue; `/ime-decompile` skill as manual backfill |
| Table `ImeVersionHistory` (partition `Global`, RowKey = version) | first/last seen, session count, `MsiArchiveStatus`, `MsiArchiveUpdatedAt`, `MsiArchiveBlobPath`, `MsiSha256`, `MsiBytes`, `MsiSourceUrl` | `RecordImeVersionAsync` (sighting), `UpdateImeVersionArchiveInfoAsync` (archive outcome / queued stamp) |

# Schema

## Sighting → job

`EventIngestProcessor` looks at the batch's `ime_agent_version` event (`agentVersion`, plus
the agent's CSP-registry enrichment `msiDownloadUrl` / `msiMatchedBy` when present).
`RecordImeVersionAsync` returns an `ImeVersionSighting`:

* `IsNew` — the insert succeeded: first fleet-wide sighting → ops event + enqueue.
* otherwise the row's `MsiArchiveStatus` / `MsiArchiveUpdatedAt`, read in the same point
  read that bumps `SessionCount` (no second table read).

A known version is **re-queued** (`ImeMsiArchiver.ShouldRequeueOnSighting`) only when all
of these hold: the event URL passes the allowlist **and** `msiMatchedBy == productVersion`
(the CSP row's ProductVersion equals the observed version, so the URL is authoritative),
the status is neither `Archived` nor `Failed:BadVersion`, and the last archive activity is
older than 24 h (`RequeueBackoff`) or never happened. The ingest path then merges
`MsiArchiveStatus = Queued` (stamping `MsiArchiveUpdatedAt`) before enqueueing, so the other
sessions of that version see "in flight" and do not pile on. Worst case for a version no
host serves any more: one walk over the hosts per day, not one per enrollment.

## Candidate hosts and the version check

Microsoft distributes IME from several versionless URLs that carry **different rollout
rings at the same time**. Observed 2026-08-29: `imeswdb-afd-secondary` served 1.104.102.0,
`imeswda-afd-primary` 1.105.101.0, `imeswda-afd-hotfix` 1.105.103.0. "A new version is by
definition what the canonical URL serves right now" is therefore false, and the first
version of the archiver filed a 1.104.102.0 package under `1.105.103.0/` (fixed by hand:
blob overwritten, row merged).

The archiver walks `BuildCandidateUrls(eventUrl)`: the allowlisted event URL first (HTTPS,
host `*.manage.microsoft.com`, filename exactly `IntuneWindowsAgent.msi`), then
`FallbackMsiUrls` = primary, secondary, hotfix — de-duplicated. For each candidate:

1. Download with the admin size cap (Content-Length preflight + mid-stream cap) into a
   delete-on-close temp file while SHA-256 hashing.
2. Read the package's `ProductVersion` with `MsiProductVersionReader` — a pure managed OLE
   compound-file reader (v3 512-byte and v4 4096-byte sectors, DIFAT/FAT/mini-FAT, MSI
   stream-name decoding, `_StringPool`/`_StringData` incl. long strings and holes, the
   column-major `Property` table with 2- or 3-byte string refs). It never throws; anything
   unreadable is `null`. Needed because the Function host is Linux — no `msiexec`.
3. `VersionsMatch(observed, productVersion)` (component-count tolerant: `1.105.103` ==
   `1.105.103.0`). Mismatch or unreadable → log, record the attempt, next candidate.
4. Match → write-once upload (If-None-Match:*), then `provenance.json` with
   `productVersion`, the chosen `url`, `urlFromEvent`, hash/size and the full `candidates`
   walk (`url`, `outcome` ∈ match / version-mismatch / too-large / download-failed /
   timeout, `productVersion`).

Outcome precedence when no candidate archived: any transient failure (download/timeout)
→ `Failed:Download` / `Failed:Timeout`, retryable via the queue's visibility ladder (the
down host might have had it); every candidate over the cap → `Failed:TooLarge`; otherwise
`Failed:VersionMismatch` — **not** retryable by the queue (retrying in minutes changes
nothing), left to the sighting re-queue above. Storage/unexpected errors stay
`Failed:Error`, retryable. A 409 on the write-once upload means an earlier attempt
archived the version: the provenance sidecar is healed from the archived bytes (hash,
size, ProductVersion) if missing — this is also how a manual skill backfill turns a null
row into `Archived` on the next authoritative sighting.

## Manual path (skill)

`/ime-decompile` downloads from the blob (`ime-blob.sh download`), and
`fetch-ime-decompile.py` names the decompile folder after the **actual** FileVersion of
the extracted binaries — so the decompile repo can never be poisoned, only the blob folder.
When the blob's version mismatches, look for `msiDownloadUrl` in *other* sessions of the
version (the first-seen session often lacks the enrichment) and fetch from that host.
`ime-blob.sh upload … --overwrite` replaces a blob known to hold the wrong build; merge the
row's `MsiSha256` / `MsiBytes` / `MsiSourceUrl` afterwards.

# Examples

* First sighting of 1.105.103.0 with no URL: primary → 1.105.101.0 (mismatch), secondary →
  1.104.102.0 (mismatch), hotfix → 1.105.103.0 (match) → archived from hotfix, provenance
  lists all three.
* Hotfix host down during the walk, others mismatching → `Failed:Download`, queue retries.
* Version 1.101.111.0 (predates the archiver, row status null): a session whose CSP row
  matches by ProductVersion arrives → status `Queued`, job runs; if no host serves it any
  more → `Failed:VersionMismatch`, next try no sooner than 24 h later.

# Citations

* `src/Backend/AutopilotMonitor.Functions/Services/Ime/ImeMsiArchiver.cs`
* `src/Backend/AutopilotMonitor.Functions/Services/Ime/MsiProductVersionReader.cs`
* `src/Backend/AutopilotMonitor.Functions/Services/Ime/ImeMsiArchiveQueueWorker.cs`
* `src/Backend/AutopilotMonitor.Functions/Services/EventIngestProcessor.cs` (ime_agent_version block)
* `src/Backend/AutopilotMonitor.Functions/Services/TableStorageService.Sessions.cs` (`RecordImeVersionAsync`, `UpdateImeVersionArchiveInfoAsync`)
* `src/Agent/AutopilotMonitor.Agent.V2.Core/Monitoring/Enrollment/SystemSignals/ImeMsiInstallSourceProbe.cs` (event enrichment)
* `.claude/commands/scripts/ime-blob.sh`, `.claude/commands/scripts/fetch-ime-decompile.py`
* Tests: `ImeMsiArchiverTests`, `MsiProductVersionReaderTests` (synthetic packages + local sweep over `ime-files/`)
