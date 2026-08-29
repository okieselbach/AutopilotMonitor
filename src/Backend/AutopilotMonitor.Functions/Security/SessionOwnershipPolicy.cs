using System;
using Azure.Data.Tables;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Security
{
    /// <summary>
    /// SESSION-OWNER-BINDING — decides whether the device behind a validated agent request is the
    /// device a session belongs to.
    /// <para>
    /// Agent endpoints authenticate at TENANT granularity (any device certificate of the tenant,
    /// or a bootstrap token) but write at SESSION granularity using a session id the caller
    /// supplies. Without this binding one enrolled device — or anyone who saw a bootstrap short
    /// code — can rewrite the timeline, flip the status and drain the pending server actions of
    /// every other session in the tenant. The session row therefore carries the identity that
    /// created it (<see cref="SessionOwner"/>), and every session-scoped write is compared against
    /// it here.
    /// </para>
    /// </summary>
    /// <remarks>
    /// STAGE 1 — SHADOW (<c>SESSION-OWNER-BINDING-SHADOW</c>): nothing is rejected. Every outcome
    /// is recorded on the request row (denominator), non-Match outcomes are logged as Warnings and
    /// would-reject outcomes raise a throttled <c>SessionOwnerMismatch</c> ops event. Bindings are
    /// stamped from stage 1 on so the data exists when enforcement is switched on.
    /// <para>
    /// Enforcement (stage 2, separate change, after a long observation window) adds a
    /// <c>Rejects</c> rule and answers a would-reject with 403 + error code
    /// <see cref="AutopilotMonitor.Shared.Constants.AgentErrorCodes.SessionOwnerMismatch"/>, on which
    /// the agent rotates its session id instead of counting an auth failure. That is what makes the
    /// one legitimate collision — an Intune re-enrollment WITHOUT a wipe, where <c>session.id</c>
    /// survives but the device gets a new certificate identity — resolve into a fresh session rather
    /// than a dead one. Deliberately no <c>Rejects</c> in this file yet: KQL written against the
    /// shadow field must keep working, and the switch has to be a visible code change.
    /// </para>
    /// <para>
    /// What this does NOT prove: the serial number is a caller-supplied header on both auth paths.
    /// A certificate holder who also knows a victim's serial can still claim a Legacy row or a
    /// bootstrap-owned row during its handoff window. Those are the accepted residuals; the
    /// TLS-proven identity closes everything else.
    /// </para>
    /// </remarks>
    public static class SessionOwnershipPolicy
    {
        /// <summary>
        /// <see cref="Microsoft.Azure.Functions.Worker.FunctionContext"/> item key under which the
        /// outcome reaches <c>RequestTelemetryMiddleware</c>, which stamps it onto the request row as
        /// the <c>SessionOwnerBinding</c> dimension — the shadow denominator (worker-side
        /// LogInformation never reaches App Insights, so the bulk Match outcome has no other carrier).
        /// </summary>
        public const string RequestItemKey = "SessionOwnerBinding";

        /// <summary>
        /// Stable outcome codes. Queried by exact match in KQL — keep the strings stable.
        /// </summary>
        public static class Outcome
        {
            /// <summary>No Sessions row yet (first registration, or ingest ahead of registration). Nothing to compare; the registration stamps the owner.</summary>
            public const string Fresh = "Fresh";

            /// <summary>Caller identity equals the stored owner — the expected case.</summary>
            public const string Match = "Match";

            /// <summary>Row predates the binding (no Owner columns) and the caller's serial equals the row's serial — claimed by this caller.</summary>
            public const string ClaimLegacy = "ClaimLegacy";

            /// <summary>Row predates the binding and the serials differ. Nothing ties the caller to the session.</summary>
            public const string LegacySerialMismatch = "LegacySerialMismatch";

            /// <summary>Cert caller, thumbprint differs, but the certificate CN (Intune device id) is the same device — re-issued certificate. Rebound.</summary>
            public const string RebindCertRotation = "RebindCertRotation";

            /// <summary>Cert caller on a bootstrap-owned session announcing the same serial — the install→runtime handoff. Rebound to the certificate.</summary>
            public const string RebindBootstrapHandoff = "RebindBootstrapHandoff";

            /// <summary>Cert caller on a bootstrap-owned session announcing a different serial.</summary>
            public const string MismatchBootstrapOwned = "MismatchBootstrapOwned";

            /// <summary>Cert caller whose thumbprint AND device id differ from the owning certificate. With serialMatch=true this is the re-enroll-without-wipe shape.</summary>
            public const string MismatchCert = "MismatchCert";

            /// <summary>Bootstrap caller on a bootstrap-owned session with a different short code or serial.</summary>
            public const string MismatchBootstrap = "MismatchBootstrap";

            /// <summary>Bootstrap caller on a cert-owned session — an auth downgrade, never legitimate.</summary>
            public const string DowngradeToBootstrap = "DowngradeToBootstrap";

            /// <summary>Validated request carried neither a certificate identity nor a bootstrap code (device validation disabled paths). Cannot bind; allowed.</summary>
            public const string CallerUnidentified = "CallerUnidentified";
        }

        /// <summary>Result of <see cref="Evaluate"/>.</summary>
        public sealed class Decision
        {
            public Decision(string outcome, SessionOwner? ownerToStamp, bool serialMatch)
            {
                Outcome = outcome;
                OwnerToStamp = ownerToStamp;
                SerialMatch = serialMatch;
            }

            /// <summary>One of the <see cref="SessionOwnershipPolicy.Outcome"/> codes.</summary>
            public string Outcome { get; }

            /// <summary>Owner the caller should write onto the row (fresh bind, legacy claim, rebind); null when the row is to be left alone.</summary>
            public SessionOwner? OwnerToStamp { get; }

            /// <summary>Whether the caller's announced serial equals the serial on the row. Diagnostic — distinguishes re-enroll-without-wipe from a foreign device on <see cref="SessionOwnershipPolicy.Outcome.MismatchCert"/>.</summary>
            public bool SerialMatch { get; }

            public bool WouldReject => WouldRejectUnderEnforcement(Outcome);
        }

        /// <summary>
        /// Evaluates the binding. Pure — no logging, no I/O, no side effects.
        /// </summary>
        /// <param name="existingRow">The Sessions row as loaded (null when absent).</param>
        /// <param name="validation">The successful security validation of the current request.</param>
        /// <param name="nowUtc">Clock for the <see cref="SessionOwner.BoundAt"/> stamp.</param>
        public static Decision Evaluate(TableEntity? existingRow, SecurityValidationResult validation, DateTime nowUtc)
        {
            var caller = FromValidation(validation, nowUtc);
            if (caller == null)
                return new Decision(Outcome.CallerUnidentified, null, false);

            if (existingRow == null)
                return new Decision(Outcome.Fresh, caller, false);

            var rowSerial = existingRow.GetString("SerialNumber");
            var serialMatch = SerialEquals(caller.Serial, rowSerial);

            var owner = FromRow(existingRow);
            if (owner == null)
            {
                return serialMatch
                    ? new Decision(Outcome.ClaimLegacy, caller, true)
                    : new Decision(Outcome.LegacySerialMismatch, null, false);
            }

            if (caller.IsCert)
            {
                if (owner.IsCert)
                {
                    if (ThumbprintEquals(caller.Thumbprint, owner.Thumbprint))
                        return new Decision(Outcome.Match, null, serialMatch);
                    if (caller.DeviceId != null && string.Equals(caller.DeviceId, owner.DeviceId, StringComparison.OrdinalIgnoreCase))
                        return new Decision(Outcome.RebindCertRotation, caller, serialMatch);
                    return new Decision(Outcome.MismatchCert, null, serialMatch);
                }

                // Bootstrap-owned row: the serial announced under the token is the only thread
                // between the two auth eras — the certificate did not exist when the row was made.
                return SerialEquals(caller.Serial, owner.Serial)
                    ? new Decision(Outcome.RebindBootstrapHandoff, caller, serialMatch)
                    : new Decision(Outcome.MismatchBootstrapOwned, null, serialMatch);
            }

            // Bootstrap caller
            if (owner.IsCert)
                return new Decision(Outcome.DowngradeToBootstrap, null, serialMatch);

            var sameCode = string.Equals(caller.BootstrapCode, owner.BootstrapCode, StringComparison.OrdinalIgnoreCase);
            return sameCode && SerialEquals(caller.Serial, owner.Serial)
                ? new Decision(Outcome.Match, null, serialMatch)
                : new Decision(Outcome.MismatchBootstrap, null, serialMatch);
        }

        /// <summary>
        /// Whether an outcome will block the request once enforcement is on. Recorded on every
        /// non-Match observation so the shadow data answers "what would we have rejected".
        /// <para>
        /// Tolerated by design (never reject): <see cref="Outcome.Fresh"/>, <see cref="Outcome.Match"/>,
        /// <see cref="Outcome.ClaimLegacy"/> (rows from before the binding must not lock their own
        /// device out), both Rebind outcomes (certificate re-issue and install→runtime handoff are
        /// normal lifecycle), and <see cref="Outcome.CallerUnidentified"/> (nothing to compare).
        /// </para>
        /// </summary>
        public static bool WouldRejectUnderEnforcement(string outcome) =>
            outcome == Outcome.LegacySerialMismatch
            || outcome == Outcome.MismatchBootstrapOwned
            || outcome == Outcome.MismatchCert
            || outcome == Outcome.MismatchBootstrap
            || outcome == Outcome.DowngradeToBootstrap;

        /// <summary>
        /// Builds the caller's identity from a successful validation. Null when the request carried
        /// no bindable identity (no certificate thumbprint and no bootstrap code).
        /// </summary>
        public static SessionOwner? FromValidation(SecurityValidationResult validation, DateTime nowUtc)
        {
            var serial = NormalizeSerial(validation.SerialNumber);

            if (validation.IsBootstrapAuth)
            {
                if (string.IsNullOrWhiteSpace(validation.BootstrapShortCode))
                    return null;
                return new SessionOwner
                {
                    Kind = SessionOwner.Kinds.Bootstrap,
                    BootstrapCode = validation.BootstrapShortCode!.Trim(),
                    Serial = serial,
                    BoundAt = nowUtc,
                };
            }

            if (string.IsNullOrWhiteSpace(validation.CertificateThumbprint))
                return null;

            return new SessionOwner
            {
                Kind = SessionOwner.Kinds.Cert,
                Thumbprint = validation.CertificateThumbprint!.Trim(),
                DeviceId = string.IsNullOrWhiteSpace(validation.IntuneDeviceId) ? null : validation.IntuneDeviceId!.Trim().ToLowerInvariant(),
                Serial = serial,
                BoundAt = nowUtc,
            };
        }

        /// <summary>Reads the owner off a Sessions row. Null when the row predates the binding.</summary>
        public static SessionOwner? FromRow(TableEntity row)
        {
            var kind = row.GetString(SessionOwner.Columns.Kind);
            if (kind != SessionOwner.Kinds.Cert && kind != SessionOwner.Kinds.Bootstrap)
                return null;

            return new SessionOwner
            {
                Kind = kind,
                Thumbprint = row.GetString(SessionOwner.Columns.Thumbprint),
                DeviceId = row.GetString(SessionOwner.Columns.DeviceId),
                BootstrapCode = row.GetString(SessionOwner.Columns.BootstrapCode),
                Serial = row.GetString(SessionOwner.Columns.Serial),
                BoundAt = row.GetDateTimeOffset(SessionOwner.Columns.BoundAt)?.UtcDateTime ?? DateTime.MinValue,
            };
        }

        /// <summary>
        /// Writes the owner columns onto <paramref name="entity"/>. Columns of the other kind are
        /// removed so a Bootstrap→Cert rebind does not leave a stale short code behind.
        /// </summary>
        public static void ApplyTo(TableEntity entity, SessionOwner owner)
        {
            foreach (var col in SessionOwner.Columns.All)
                entity.Remove(col);

            entity[SessionOwner.Columns.Kind] = owner.Kind;
            if (!string.IsNullOrEmpty(owner.Thumbprint)) entity[SessionOwner.Columns.Thumbprint] = owner.Thumbprint;
            if (!string.IsNullOrEmpty(owner.DeviceId)) entity[SessionOwner.Columns.DeviceId] = owner.DeviceId;
            if (!string.IsNullOrEmpty(owner.BootstrapCode)) entity[SessionOwner.Columns.BootstrapCode] = owner.BootstrapCode;
            if (!string.IsNullOrEmpty(owner.Serial)) entity[SessionOwner.Columns.Serial] = owner.Serial;
            entity[SessionOwner.Columns.BoundAt] = new DateTimeOffset(DateTime.SpecifyKind(owner.BoundAt, DateTimeKind.Utc));
        }

        private static string? NormalizeSerial(string? serial)
            => string.IsNullOrWhiteSpace(serial) ? null : serial!.Trim();

        private static bool SerialEquals(string? a, string? b)
        {
            var na = NormalizeSerial(a);
            var nb = NormalizeSerial(b);
            if (na == null || nb == null) return false;
            return string.Equals(na, nb, StringComparison.OrdinalIgnoreCase);
        }

        private static bool ThumbprintEquals(string? a, string? b)
            => !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b)
               && string.Equals(a!.Trim(), b!.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
