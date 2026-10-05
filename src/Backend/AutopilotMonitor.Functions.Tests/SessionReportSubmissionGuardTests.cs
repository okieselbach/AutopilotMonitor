using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// The two guards both report routes share: comment/contact limits checked before the upload, and
/// the SessionReports row write that may no longer fail silently (the report would be invisible in
/// the operator list while the submitter was told it arrived).
/// </summary>
public sealed class SessionReportSubmissionGuardTests
{
    [Fact]
    public void Comment_AtTheLimit_Passes_OneMoreCharacter_IsRejected()
    {
        var max = Constants.SubmissionLimits.ReportCommentMaxChars;

        Assert.Null(SessionReportService.ValidateSubmissionText(new string('x', max), null));
        Assert.NotNull(SessionReportService.ValidateSubmissionText(new string('x', max + 1), null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("alice.support@contoso.invalid")]
    public void MissingOrValidContactAddress_Passes(string? email)
        => Assert.Null(SessionReportService.ValidateSubmissionText("Installation stuck in account setup.", email));

    [Theory]
    [InlineData("not-an-address")]
    [InlineData("alice@contoso.invalid; bob@contoso.invalid")]
    [InlineData("alice@contoso.invalid\r\nBcc: mallory@contoso.invalid")]
    public void InvalidContactAddress_IsRejected(string email)
        => Assert.NotNull(SessionReportService.ValidateSubmissionText(null, email));

    [Fact]
    public async Task StoredRow_KeepsTheBlobs()
    {
        var deleted = new List<string>();

        await SessionReportService.RecordOrRollBackAsync(
            () => Task.FromResult(true), Track(deleted), NullLogger.Instance, "abc123", "report.zip", "archive.zip");

        Assert.Empty(deleted);
    }

    [Fact]
    public async Task FailedRow_FailsTheRequest_AndRemovesEveryUploadedBlob()
    {
        var deleted = new List<string>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => SessionReportService.RecordOrRollBackAsync(
            () => Task.FromResult(false), Track(deleted), NullLogger.Instance, "abc123", "report.zip", null, "archive.zip"));

        Assert.Equal(new[] { "report.zip", "archive.zip" }, deleted);
    }

    [Fact]
    public async Task FailedRow_StillFails_WhenTheCleanupThrows()
    {
        var attempted = new List<string>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => SessionReportService.RecordOrRollBackAsync(
            () => Task.FromResult(false),
            name =>
            {
                attempted.Add(name);
                throw new InvalidOperationException("simulated blob outage");
            },
            NullLogger.Instance, "abc123", "report.zip", "archive.zip"));

        Assert.Equal(new[] { "report.zip", "archive.zip" }, attempted);
    }

    private static Func<string, Task> Track(List<string> deleted) => name =>
    {
        deleted.Add(name);
        return Task.CompletedTask;
    };
}
