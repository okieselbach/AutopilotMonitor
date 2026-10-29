using Azure.Data.Tables;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// D-393: a failed IME app row whose failure event carried no code takes IME's code from the
/// registry. The log line that first marks the failure ("Installation is timeout") has no code;
/// IME's code follows seconds later in its report and in the registry, but the app is already
/// Error, so the agent sends no second event. Pins the pure pieces behind
/// <see cref="TableStorageService.ResolveRegistryFailureCodesForSessionAsync"/>: which registry
/// code counts (<see cref="TableStorageService.LatestRegistryErrorCodes"/>), which rows may be
/// filled (<see cref="TableStorageService.IsRegistryFailureCodeCandidate"/>) and the attempt
/// boundary (<see cref="TableStorageService.RegistryFailureCodeFor"/>).
/// </summary>
public class AppInstallRegistryFailureCodeTests
{
    private static readonly DateTime T0 = new(2026, 10, 29, 21, 15, 0, DateTimeKind.Utc);

    private const string IdA = "aaaaaaaa-1111-2222-3333-444444444444";
    private const string IdB = "bbbbbbbb-1111-2222-3333-444444444444";

    // Field case 2026-10-29: IME-ERROR-TIMEOUT without a code, the registry 13 s later.
    private const string ProcessTimeout = "-2016214839";      // 0x87D300C9
    private const string NotDetectedAfterInstall = "-2016345060"; // 0x87D1041C

    private static EnrollmentEvent Registry(
        string appId, string enforcementClass, object? errorCode, DateTime at, long sequence = 1)
    {
        var data = new Dictionary<string, object>
        {
            ["appId"] = appId,
            ["enforcementClass"] = enforcementClass,
        };
        if (errorCode != null) data["errorCode"] = errorCode;
        return new EnrollmentEvent
        {
            EventType = Constants.EventTypes.RegistryAppState,
            Timestamp = at,
            Sequence = sequence,
            Data = data,
        };
    }

    private static TableEntity Row(
        string? appId = IdA,
        string status = "Failed",
        string? failureCode = null,
        string? source = null,
        bool collision = false,
        DateTime? lastAttemptStartedAt = null,
        DateTime? startedAt = null)
    {
        var row = new TableEntity("tenant", "session_App") { ["Status"] = status };
        if (appId != null) row["AppId"] = appId;
        if (failureCode != null) row["FailureCode"] = failureCode;
        if (source != null) row["Source"] = source;
        if (collision) row["AppIdCollision"] = true;
        if (lastAttemptStartedAt.HasValue) row["LastAttemptStartedAt"] = new DateTimeOffset(lastAttemptStartedAt.Value);
        if (startedAt.HasValue) row["StartedAt"] = new DateTimeOffset(startedAt.Value);
        return row;
    }

    // ── which registry code counts ──────────────────────────────────────────

    [Fact]
    public void Codes_LatestErrorPerApp_WinsInAnyInputOrder_AndIsCanonical()
    {
        var older = Registry(IdA, "error", ProcessTimeout, T0);
        var newer = Registry(IdA, "error", NotDetectedAfterInstall, T0.AddMinutes(5));

        foreach (var events in new[] { new[] { older, newer }, new[] { newer, older } })
        {
            var codes = TableStorageService.LatestRegistryErrorCodes(events);
            Assert.Equal(("0x87d1041c", T0.AddMinutes(5)), codes[IdA]);
        }
    }

    [Fact]
    public void Codes_SameTimestamp_HigherSequenceWins()
    {
        var codes = TableStorageService.LatestRegistryErrorCodes(new[]
        {
            Registry(IdA, "error", NotDetectedAfterInstall, T0, sequence: 9),
            Registry(IdA, "error", ProcessTimeout, T0, sequence: 4),
        });

        Assert.Equal("0x87d1041c", codes[IdA].Code);
    }

    [Fact]
    public void Codes_OnlyErrorStatesWithANonZeroCodeCount()
    {
        var codes = TableStorageService.LatestRegistryErrorCodes(new[]
        {
            Registry(IdA, "inProgress", ProcessTimeout, T0),
            Registry(IdA, "success", "0", T0),
            Registry(IdA, "error", null, T0),     // WinGet report without a code: the registry has none either
            Registry(IdA, "error", "0", T0),
            Registry(IdA, "error", "not-a-code", T0),
        });

        Assert.Empty(codes);
    }

    [Fact]
    public void Codes_ReadNativeNumbers_AndMatchAppIdsCaseInsensitively()
    {
        // Stored event data comes back as native .NET values, not only as strings.
        var codes = TableStorageService.LatestRegistryErrorCodes(new[]
        {
            Registry(IdA.ToUpperInvariant(), "error", -2016214839L, T0),
        });

        Assert.Equal("0x87d300c9", codes[IdA].Code);
    }

    // ── which rows may be filled ────────────────────────────────────────────

    [Fact]
    public void Candidate_FailedImeRowWithAppIdAndNoCode()
    {
        Assert.True(TableStorageService.IsRegistryFailureCodeCandidate(Row()));
        Assert.True(TableStorageService.IsRegistryFailureCodeCandidate(Row(source: "ime")));
    }

    [Fact]
    public void Candidate_NeverOtherChannelsStatesIdentitiesOrRowsWithACode()
    {
        Assert.False(TableStorageService.IsRegistryFailureCodeCandidate(Row(source: "realmjoin")));
        Assert.False(TableStorageService.IsRegistryFailureCodeCandidate(Row(status: "Succeeded")));
        Assert.False(TableStorageService.IsRegistryFailureCodeCandidate(Row(status: "Incomplete")));
        Assert.False(TableStorageService.IsRegistryFailureCodeCandidate(Row(failureCode: "1603")));            // event code stays
        Assert.False(TableStorageService.IsRegistryFailureCodeCandidate(Row(failureCode: "esp_apps_timeout"))); // failureType stays
        Assert.False(TableStorageService.IsRegistryFailureCodeCandidate(Row(appId: null)));
        Assert.False(TableStorageService.IsRegistryFailureCodeCandidate(Row(collision: true)));
    }

    // ── the attempt boundary ────────────────────────────────────────────────

    [Fact]
    public void FailureCode_FilledFromTheRegistryErrorOfTheLastAttempt()
    {
        var codes = TableStorageService.LatestRegistryErrorCodes(new[]
        {
            Registry(IdA, "error", ProcessTimeout, T0.AddMinutes(60)),
        });

        Assert.Equal("0x87d300c9", TableStorageService.RegistryFailureCodeFor(Row(lastAttemptStartedAt: T0), codes));
    }

    [Fact]
    public void FailureCode_RegistryErrorOfAnEarlierAttempt_IsNotUsed()
    {
        var codes = TableStorageService.LatestRegistryErrorCodes(new[]
        {
            Registry(IdA, "error", ProcessTimeout, T0),
        });

        Assert.Null(TableStorageService.RegistryFailureCodeFor(Row(lastAttemptStartedAt: T0.AddMinutes(5)), codes));
        // Without an attempt anchor the first observation bounds it.
        Assert.Null(TableStorageService.RegistryFailureCodeFor(Row(startedAt: T0.AddMinutes(5)), codes));
        Assert.Equal("0x87d300c9", TableStorageService.RegistryFailureCodeFor(Row(startedAt: T0.AddMinutes(-5)), codes));
    }

    [Fact]
    public void FailureCode_NoObservedStart_TakesTheRegistryCode()
    {
        var codes = TableStorageService.LatestRegistryErrorCodes(new[]
        {
            Registry(IdA, "error", ProcessTimeout, T0),
        });

        Assert.Equal("0x87d300c9", TableStorageService.RegistryFailureCodeFor(Row(), codes));
    }

    [Fact]
    public void FailureCode_OtherAppOrNonCandidate_StaysEmpty()
    {
        var codes = TableStorageService.LatestRegistryErrorCodes(new[]
        {
            Registry(IdB, "error", ProcessTimeout, T0),
        });

        Assert.Null(TableStorageService.RegistryFailureCodeFor(Row(), codes));
        Assert.Null(TableStorageService.RegistryFailureCodeFor(Row(appId: IdB, failureCode: "0x87d1041c"), codes));
    }
}
