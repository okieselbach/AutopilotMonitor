# Backend Unit Tests

## Overview

This test suite protects the Azure Functions backend against regressions in security validation, data parsing, rate limiting, and the event ingestion pipeline.

**Framework:** xUnit 2.9.2 | **Target:** .NET 8.0 | **Mocking:** None (pure logic tests with real objects)

## Test Suites

### EventTimestampValidationTests (34 tests)

**Protects against:** Timestamp overflow causing Azure Table Storage failures, timezone drift corrupting event timelines, extreme dates crashing the ingest pipeline.

| Area | What it tests |
|------|--------------|
| `SanitizeTimestamp` | Clamps DateTime.MinValue/MaxValue, handles far-future dates, converts Unspecified/Local to UTC |
| `IsReasonableTimestamp` | Boundary checks for valid date ranges |
| `SafeDurationSeconds` | Reversed timestamps, overflow prevention, max duration clamping |
| `SanitizeEventTimestamps` | Pipeline integration: flags sanitized events, preserves originals |

**Production bug prevented:** Timestamp overflow (DateTime.MaxValue) crashed Azure Table Storage writes. Tests enforce clamping before storage.

### DistressRateLimitServiceTests (21 tests)

**Protects against:** DDoS on the unauthenticated distress endpoint, per-IP abuse, tenant-level flooding.

| Layer | Limits | What it tests |
|-------|--------|--------------|
| Per-IP | 5 req/15 min | Exact boundary, independent IP tracking |
| Per-Tenant | 20 req/1 hour | Exact boundary, independent tenant tracking |
| Global Circuit Breaker | 200 req/min | Trips at threshold, blocks all subsequent |
| Layer Interaction | - | Circuit breaker checked first, IP rejection doesn't increment tenant counter |

### SecurityValidatorTests (17 tests)

**Protects against:** GUID injection attacks, malformed TenantId/SessionId values bypassing validation.

| Area | What it tests |
|------|--------------|
| `IsValidGuid` | Valid/invalid formats, null/empty/whitespace, braced format rejection |
| `EnsureValidGuid` | Exception throwing with parameter names |

### DistressValidationTests (51 tests)

**Protects against:** Input injection on unauthenticated endpoint, control character injection, timestamp spoofing, IP parsing bypasses.

| Area | What it tests |
|------|--------------|
| `ParseIpFromForwardedFor` | IPv4/IPv6 parsing, multiple proxies, port stripping |
| `Sanitize` | Control character stripping, field-specific max lengths, post-strip truncation |
| `GuidPattern` | SQL injection, path traversal, non-hex character rejection |
| `IsDistressTimestampValid` | Future/past boundaries, extreme value rejection |

### IngestCriticalPathTests (10 tests)

**Protects against:** Missing server-stamped fields causing 500 errors, TenantId mismatch between metadata and events.

| Area | What it tests |
|------|--------------|
| `StampServerFields` | Sets ReceivedAt/TenantId/SessionId, overrides null/wrong values |
| Pipeline Integration | Full NDJSON -> Stamp -> Validate flow without errors |
| TenantId Mismatch | Case-insensitive comparison |

**Production bug prevented:** Events without server-stamped TenantId caused `EnsureValidGuid` to throw on null, returning 500 instead of processing events.

### BuiltInRulesTests (5 tests)

**Protects against:** Embedded resource loading failures after build changes, missing required fields in rule definitions, invalid pattern categories.

| Area | What it tests |
|------|--------------|
| `BuiltInGatherRules` | Non-empty load, required fields per rule |
| `BuiltInAnalyzeRules` | Minimum count (18+), required fields, IsBuiltIn flag |
| `BuiltInImeLogPatterns` | Minimum count (47+), valid actions set (25), valid categories |

### EndpointPolicyCatalogCompletenessTests (5 tests)

**Protects against:** New HTTP endpoints missing security policy registration (fail-closed -> 403), stale catalog entries after endpoint removal.

| Area | What it tests |
|------|--------------|
| Route Coverage | All `[HttpTrigger]` routes have catalog entries (reflection-based scan) |
| Catalog Hygiene | No orphaned entries for removed routes |
| Parameterized Routes | `{id}`, `{code}`, `{tenantId}` etc. resolve correctly |
| Method Disambiguation | Same route with different HTTP methods gets correct policy |

## Running Tests

```bash
dotnet test src/Backend/AutopilotMonitor.Functions.Tests/ --nologo -v quiet
```
