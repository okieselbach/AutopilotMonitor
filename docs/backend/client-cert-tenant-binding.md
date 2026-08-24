---
type: Concept
title: Client-Certificate Tenant Binding — Closing the Cross-Tenant Gap
description: Why a chain-valid Intune MDM certificate does not prove the caller belongs to the tenant it claims, which certificate extension carries the Entra tenant id (and which two look like it but do not), and the shadow-then-enforce rollout that measures field coverage before it can lock devices out.
resource: src/Backend/AutopilotMonitor.Functions/Security/CertTenantBinding.cs
tags:
  - backend
  - security
  - authentication
  - certificates
  - intune
  - multi-tenant
timestamp: 2026-08-24T00:00:00+02:00
---

# Problem

Agent requests authenticate with mTLS. `CertificateValidator` pins the chain to the embedded
Intune roots (`X509ChainTrustMode.CustomRootTrust`), checks the validity window and requires the
Client-Authentication EKU. All of that together proves exactly one thing: **the certificate was
issued by the Microsoft Intune MDM Device CA to some tenant**.

Those roots are shared by every Intune tenant on the planet. Nothing in the certificate stage ties
the caller to the tenant in the request. The tenant scoping came entirely from the downstream
device validators — the serial number must appear in that tenant's Autopilot / imported-device /
Cloud PC inventory. Device serial numbers are not secrets; they are printed on the chassis.

So the pre-existing attack was: enroll a device in your own Intune tenant, obtain a legitimate
device certificate, learn one serial number of the victim tenant, and submit telemetry as that
tenant. Certificate validation passes, device validation passes.

# Mechanism

The Intune MDM Device CA stamps the customer's identifiers into the leaf certificate. Since the
agent presents this certificate in the TLS handshake, its contents are proof of possession and the
backend can compare them against the requested tenant — no Graph call, no additional consent, and
nothing the agent has to send.

## Which extension carries what

Verified by decoding a real field certificate (issued 2026-05-08, `Functions.Tests/device-cert-sample.pem`):

| OID | Encoding | Content |
| --- | --- | --- |
| `1.2.840.113556.5.14` | nested OCTET STRING (`04 10` + 16 bytes) | **Entra tenant id** — the one to compare |
| `1.2.840.113556.5.6` | nested OCTET STRING (`04 10` + 16 bytes) | Intune **Account** id — a *different* GUID, never equal to the tenant id |
| `1.2.840.113556.5.4` | **16 raw bytes, no wrapper** | Intune device id, identical to the certificate's Subject CN |

Two traps are worth stating explicitly, because both cost real debugging time:

1. `…5.6` is widely described as "the tenant id (AccountID)". It is not the Entra tenant id.
   Comparing it against the requested tenant would reject every request in production.
2. The encodings are not uniform. `…5.4` holds the GUID bare while the other two nest it in a
   second OCTET STRING, so a decoder written against one shape silently fails on the other.
   `MsDeviceCertificateOids.TryParseGuid` accepts both, and all GUIDs are Microsoft
   little-endian (what `new Guid(byte[])` expects).

A 16-byte input is inherently ambiguous — it is indistinguishable from a truncated wrapper and is
always read as a bare GUID. That is harmless here: the value is only ever compared for equality
against a known tenant id, never trusted as a standalone claim.

## Where it is NOT

`EntraDeviceCertHelper` reads tenant and device ids from the **MS-Organization-Access** certificate
(`1.2.840.113556.1.5.284.*`). That certificate is not part of the authentication path at all — the
agent selects the Intune MDM certificate by exact issuer match (`CN=Microsoft Intune MDM Device CA`,
see `CertificateHelper.SelectMdmCertificate`) and the backend pins only the MDM chain. The Entra
certificate is used purely as a local fallback for `TenantIdResolver`; it never leaves the device.

Sending it in a header instead would prove nothing: only the certificate used in the TLS handshake
demonstrates possession of a private key. A certificate copied into a custom header can be replayed
by anyone who has seen it once.

# Rollout

Enforcing immediately would be reckless: any device population whose certificates predate the
extension, or whose CA behaves differently (sovereign clouds), would be locked out at once. The
rollout therefore has staged consequences.

**Stage 1 — shadow (current).** `SecurityValidator.ObserveCertTenantBinding` evaluates the binding
after certificate validation and logs the outcome. It returns `void`, changes no state, and swallows
its own exceptions, so it cannot fail a request that would otherwise be accepted. The pure comparison
lives in `CertTenantBinding.Evaluate`; the outcome codes (`Match`, `Mismatch`, `ExtensionMissing`,
`Unparseable`, `RequestTenantNotAGuid`) are stable and queried by exact match in KQL.
`WouldRejectUnderEnforcement` records what stage 2 *would* have done, so the telemetry can be read
without re-deriving the rule at query time.

The telemetry uses two carriers, because the numerator and the denominator have very different
costs and answer different questions:

* **Every** outcome, `Match` included, is stamped onto the request row as the `CertTenantBinding`
  dimension — `SecurityValidator` writes it into `FunctionContext.Items`
  (`CertTenantBinding.RequestItemKey`) and `RequestTelemetryMiddleware` copies it onto the
  `RequestTelemetry` it already emits. That row exists per request and is unsampled, so the
  denominator costs no additional telemetry at all.
* Outcomes that need an enforcement decision (mismatch, missing, undecodable) are **additionally**
  logged at Warning with the full context — thumbprint, certificate tenant, session.

`Match` is deliberately not a trace line. Worker-side `LogInformation` never reaches App Insights
(the ApplicationInsightsLoggerProvider default rule is Warning+ and host-forwarding of worker logs
is disabled), so a `Match` trace is dead code that silently costs the denominator. The first
deployment of this feature made exactly that mistake: it emitted zero rows across 32k requests,
which is indistinguishable from the code never running.

**Stage 2 — enforce.** Once telemetry shows the `ExtensionMissing` rate is negligible, `Mismatch`
becomes a rejection. The decision that still has to be made is the policy for certificates without
the extension: skip with a warning, or reject. Grep marker for every stage-2 site:
`CERT-TENANT-BINDING-SHADOW`.

The bootstrap-token path (`X-Bootstrap-Token`) bypasses certificate validation by design and is
unaffected by either stage.

# Examples

Coverage — the question stage 2 depends on ("would enforcing lock anyone out?"):

```kusto
requests
| where isnotempty(customDimensions.CertTenantBinding)
| summarize Requests = sum(itemCount)
    by Outcome = tostring(customDimensions.CertTenantBinding),
       Tenant  = tostring(customDimensions.TenantId)
| order by Requests desc
```

Detail on the individually actionable ones:

```kusto
traces
| where message has "AgentCertTenantBinding"
| extend Outcome = tostring(customDimensions.Outcome),
         Tenant  = tostring(customDimensions.TenantId)
| summarize count() by Outcome, Tenant
```

Anything in `Mismatch` is a cross-tenant certificate; anything in `ExtensionMissing` is a device
that stage-2 enforcement would have to make a policy decision about. Note that the trace query
alone can never show `Match`, so it cannot tell "everything matched" apart from "nothing ran" —
use the request-row query for that.

# Citations

* `src/Shared/AutopilotMonitor.Shared/Security/MsDeviceCertificateOids.cs` — OID catalog and the
  shared ASN.1 GUID decoder (one implementation for agent and backend).
* `src/Backend/AutopilotMonitor.Functions/Security/CertificateValidator.cs` — extension extraction
  alongside chain validation, cached per thumbprint.
* `src/Backend/AutopilotMonitor.Functions/Security/CertTenantBinding.cs` — the pure comparison.
* `src/Backend/AutopilotMonitor.Functions/Security/SecurityValidator.cs` — shadow observation.
* [W365 Cloud PC Device Validation](cloudpc-device-validation.md) — the other consumer of this
  certificate's identity fields (Subject CN = Intune device id).
