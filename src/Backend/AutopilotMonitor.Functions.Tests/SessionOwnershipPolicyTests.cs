using System;
using System.Linq;
using System.Reflection;
using Azure.Data.Tables;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// SESSION-OWNER-BINDING rule matrix. The policy is pure, so every lifecycle shape that must keep
/// working (restart, WhiteGlove Part 2, bootstrap→cert handoff, certificate re-issue, rows from
/// before the binding) and every shape that must be visible as a would-reject (foreign cert,
/// foreign bootstrap code, bootstrap on a cert-owned row, re-enroll without wipe) is pinned here.
/// </summary>
public class SessionOwnershipPolicyTests
{
    private static readonly DateTime Now = new(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc);
    private const string Thumb1 = "AA11BB22CC33DD44EE55FF6677889900AABBCCDD";
    private const string Thumb2 = "0011223344556677889900AABBCCDDEEFF001122";
    private const string Dev1 = "0f4e1c2d-1111-4aaa-8bbb-000000000001";
    private const string Dev2 = "0f4e1c2d-2222-4aaa-8bbb-000000000002";

    private static SecurityValidationResult Cert(string thumb, string? deviceId, string? serial = "SN-1") => new()
    {
        IsValid = true,
        CertificateThumbprint = thumb,
        IntuneDeviceId = deviceId,
        SerialNumber = serial,
        ValidatedBy = ValidatorType.AutopilotV1,
    };

    private static SecurityValidationResult Bootstrap(string code, string? serial = "SN-1") => new()
    {
        IsValid = true,
        IsBootstrapAuth = true,
        BootstrapShortCode = code,
        SerialNumber = serial,
        ValidatedBy = ValidatorType.Bootstrap,
    };

    private static TableEntity Row(string? serial = "SN-1", SessionOwner? owner = null)
    {
        var row = new TableEntity("t", "s") { ["SerialNumber"] = serial ?? string.Empty };
        if (owner != null) SessionOwnershipPolicy.ApplyTo(row, owner);
        return row;
    }

    private static SessionOwner CertOwner(string thumb, string? deviceId, string serial = "SN-1") =>
        new() { Kind = SessionOwner.Kinds.Cert, Thumbprint = thumb, DeviceId = deviceId, Serial = serial, BoundAt = Now };

    private static SessionOwner BootstrapOwner(string code, string serial = "SN-1") =>
        new() { Kind = SessionOwner.Kinds.Bootstrap, BootstrapCode = code, Serial = serial, BoundAt = Now };

    // ── no row / unidentified ────────────────────────────────────────────────

    [Fact]
    public void No_row_is_Fresh_and_yields_the_caller_as_owner()
    {
        var d = SessionOwnershipPolicy.Evaluate(null, Cert(Thumb1, Dev1), Now);
        Assert.Equal(SessionOwnershipPolicy.Outcome.Fresh, d.Outcome);
        Assert.NotNull(d.OwnerToStamp);
        Assert.Equal(SessionOwner.Kinds.Cert, d.OwnerToStamp!.Kind);
        Assert.Equal(Thumb1, d.OwnerToStamp.Thumbprint);
        Assert.Equal(Dev1, d.OwnerToStamp.DeviceId);
        Assert.Equal("SN-1", d.OwnerToStamp.Serial);
        Assert.False(d.WouldReject);
    }

    [Fact]
    public void Validation_without_thumbprint_or_code_is_CallerUnidentified_and_never_rejects()
    {
        var v = new SecurityValidationResult { IsValid = true, SerialNumber = "SN-1" };
        var d = SessionOwnershipPolicy.Evaluate(Row(owner: CertOwner(Thumb1, Dev1)), v, Now);
        Assert.Equal(SessionOwnershipPolicy.Outcome.CallerUnidentified, d.Outcome);
        Assert.Null(d.OwnerToStamp);
        Assert.False(d.WouldReject);
    }

    // ── legacy rows (pre-binding) ────────────────────────────────────────────

    [Fact]
    public void Legacy_row_with_same_serial_is_claimed()
    {
        var d = SessionOwnershipPolicy.Evaluate(Row(serial: "sn-1"), Cert(Thumb1, Dev1, "SN-1 "), Now);
        Assert.Equal(SessionOwnershipPolicy.Outcome.ClaimLegacy, d.Outcome);
        Assert.NotNull(d.OwnerToStamp);
        Assert.True(d.SerialMatch);
        Assert.False(d.WouldReject);
    }

    [Fact]
    public void Legacy_row_with_different_serial_would_reject_and_is_not_claimed()
    {
        var d = SessionOwnershipPolicy.Evaluate(Row(serial: "OTHER"), Cert(Thumb1, Dev1), Now);
        Assert.Equal(SessionOwnershipPolicy.Outcome.LegacySerialMismatch, d.Outcome);
        Assert.Null(d.OwnerToStamp);
        Assert.True(d.WouldReject);
    }

    [Fact]
    public void Legacy_row_without_serial_cannot_be_claimed()
    {
        var d = SessionOwnershipPolicy.Evaluate(Row(serial: ""), Cert(Thumb1, Dev1), Now);
        Assert.Equal(SessionOwnershipPolicy.Outcome.LegacySerialMismatch, d.Outcome);
    }

    // ── cert caller ──────────────────────────────────────────────────────────

    [Fact]
    public void Same_thumbprint_is_Match_case_insensitively()
    {
        var d = SessionOwnershipPolicy.Evaluate(Row(owner: CertOwner(Thumb1, Dev1)), Cert(Thumb1.ToLowerInvariant(), Dev1), Now);
        Assert.Equal(SessionOwnershipPolicy.Outcome.Match, d.Outcome);
        Assert.Null(d.OwnerToStamp);
    }

    [Fact]
    public void Reissued_certificate_of_the_same_device_rebinds()
    {
        var d = SessionOwnershipPolicy.Evaluate(Row(owner: CertOwner(Thumb1, Dev1)), Cert(Thumb2, Dev1.ToUpperInvariant()), Now);
        Assert.Equal(SessionOwnershipPolicy.Outcome.RebindCertRotation, d.Outcome);
        Assert.Equal(Thumb2, d.OwnerToStamp!.Thumbprint);
        Assert.Equal(Dev1, d.OwnerToStamp.DeviceId);
        Assert.False(d.WouldReject);
    }

    [Fact]
    public void Foreign_certificate_is_MismatchCert_with_serialMatch_false()
    {
        var d = SessionOwnershipPolicy.Evaluate(Row(owner: CertOwner(Thumb1, Dev1)), Cert(Thumb2, Dev2, "SN-ATTACKER"), Now);
        Assert.Equal(SessionOwnershipPolicy.Outcome.MismatchCert, d.Outcome);
        Assert.False(d.SerialMatch);
        Assert.Null(d.OwnerToStamp);
        Assert.True(d.WouldReject);
    }

    [Fact]
    public void Reenroll_without_wipe_is_MismatchCert_with_serialMatch_true()
    {
        // Same chassis (serial), new Intune device id + new certificate, old session.id on disk.
        var d = SessionOwnershipPolicy.Evaluate(Row(owner: CertOwner(Thumb1, Dev1)), Cert(Thumb2, Dev2, "SN-1"), Now);
        Assert.Equal(SessionOwnershipPolicy.Outcome.MismatchCert, d.Outcome);
        Assert.True(d.SerialMatch);
        Assert.True(d.WouldReject);
    }

    [Fact]
    public void Cert_without_device_id_never_rebinds_on_thumbprint_change()
    {
        var d = SessionOwnershipPolicy.Evaluate(Row(owner: CertOwner(Thumb1, null)), Cert(Thumb2, null), Now);
        Assert.Equal(SessionOwnershipPolicy.Outcome.MismatchCert, d.Outcome);
    }

    [Fact]
    public void Cert_on_bootstrap_owned_row_with_same_serial_is_the_handoff_and_rebinds_to_cert()
    {
        var d = SessionOwnershipPolicy.Evaluate(Row(owner: BootstrapOwner("ABC123")), Cert(Thumb1, Dev1), Now);
        Assert.Equal(SessionOwnershipPolicy.Outcome.RebindBootstrapHandoff, d.Outcome);
        Assert.Equal(SessionOwner.Kinds.Cert, d.OwnerToStamp!.Kind);
        Assert.Null(d.OwnerToStamp.BootstrapCode);
        Assert.False(d.WouldReject);
    }

    [Fact]
    public void Cert_on_bootstrap_owned_row_with_other_serial_would_reject()
    {
        var d = SessionOwnershipPolicy.Evaluate(Row(owner: BootstrapOwner("ABC123")), Cert(Thumb1, Dev1, "OTHER"), Now);
        Assert.Equal(SessionOwnershipPolicy.Outcome.MismatchBootstrapOwned, d.Outcome);
        Assert.True(d.WouldReject);
    }

    // ── bootstrap caller ─────────────────────────────────────────────────────

    [Fact]
    public void Bootstrap_same_code_and_serial_is_Match()
    {
        var d = SessionOwnershipPolicy.Evaluate(Row(owner: BootstrapOwner("ABC123")), Bootstrap("abc123"), Now);
        Assert.Equal(SessionOwnershipPolicy.Outcome.Match, d.Outcome);
    }

    [Theory]
    [InlineData("XYZ789", "SN-1")]
    [InlineData("ABC123", "SN-2")]
    public void Bootstrap_with_other_code_or_serial_would_reject(string code, string serial)
    {
        var d = SessionOwnershipPolicy.Evaluate(Row(owner: BootstrapOwner("ABC123")), Bootstrap(code, serial), Now);
        Assert.Equal(SessionOwnershipPolicy.Outcome.MismatchBootstrap, d.Outcome);
        Assert.True(d.WouldReject);
    }

    [Fact]
    public void Bootstrap_on_cert_owned_row_is_a_downgrade_and_would_reject()
    {
        var d = SessionOwnershipPolicy.Evaluate(Row(owner: CertOwner(Thumb1, Dev1)), Bootstrap("ABC123"), Now);
        Assert.Equal(SessionOwnershipPolicy.Outcome.DowngradeToBootstrap, d.Outcome);
        Assert.True(d.WouldReject);
    }

    [Fact]
    public void Bootstrap_validation_without_short_code_is_unidentified()
    {
        var v = new SecurityValidationResult { IsValid = true, IsBootstrapAuth = true, SerialNumber = "SN-1" };
        Assert.Equal(SessionOwnershipPolicy.Outcome.CallerUnidentified, SessionOwnershipPolicy.Evaluate(Row(), v, Now).Outcome);
    }

    // ── row round-trip ───────────────────────────────────────────────────────

    [Fact]
    public void ApplyTo_and_FromRow_round_trip_and_clear_the_other_kind()
    {
        var row = Row(owner: BootstrapOwner("ABC123"));
        SessionOwnershipPolicy.ApplyTo(row, CertOwner(Thumb1, Dev1));

        Assert.False(row.ContainsKey(SessionOwner.Columns.BootstrapCode));
        var back = SessionOwnershipPolicy.FromRow(row)!;
        Assert.Equal(SessionOwner.Kinds.Cert, back.Kind);
        Assert.Equal(Thumb1, back.Thumbprint);
        Assert.Equal(Dev1, back.DeviceId);
        Assert.Equal("SN-1", back.Serial);
        Assert.Equal(Now, back.BoundAt);
        Assert.Equal(DateTimeKind.Utc, back.BoundAt.Kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Unknown")]
    public void FromRow_treats_unknown_kind_as_legacy(string? kind)
    {
        var row = Row();
        if (kind != null) row[SessionOwner.Columns.Kind] = kind;
        Assert.Null(SessionOwnershipPolicy.FromRow(row));
    }

    // ── enforcement rule pins ────────────────────────────────────────────────

    [Theory]
    [InlineData(SessionOwnershipPolicy.Outcome.Fresh)]
    [InlineData(SessionOwnershipPolicy.Outcome.Match)]
    [InlineData(SessionOwnershipPolicy.Outcome.ClaimLegacy)]
    [InlineData(SessionOwnershipPolicy.Outcome.RebindCertRotation)]
    [InlineData(SessionOwnershipPolicy.Outcome.RebindBootstrapHandoff)]
    [InlineData(SessionOwnershipPolicy.Outcome.CallerUnidentified)]
    public void Lifecycle_outcomes_are_deliberately_tolerated(string outcome)
        => Assert.False(SessionOwnershipPolicy.WouldRejectUnderEnforcement(outcome));

    [Theory]
    [InlineData(SessionOwnershipPolicy.Outcome.LegacySerialMismatch)]
    [InlineData(SessionOwnershipPolicy.Outcome.MismatchBootstrapOwned)]
    [InlineData(SessionOwnershipPolicy.Outcome.MismatchCert)]
    [InlineData(SessionOwnershipPolicy.Outcome.MismatchBootstrap)]
    [InlineData(SessionOwnershipPolicy.Outcome.DowngradeToBootstrap)]
    public void Foreign_identity_outcomes_would_reject(string outcome)
        => Assert.True(SessionOwnershipPolicy.WouldRejectUnderEnforcement(outcome));

    [Fact]
    public void Stage1_has_no_Rejects_rule()
    {
        // Enforcement is a deliberate, visible code change (stage 2) — not something that can
        // slip in through a refactor. Delete this test in the change that adds Rejects.
        var rejects = typeof(SessionOwnershipPolicy).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == "Rejects").ToArray();
        Assert.Empty(rejects);
    }
}
