using System.Text.Json;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// The two events that say the software-to-CPE mappings are not what they should be: the table
/// could not be read (<see cref="OpsEventService.RecordCpeMappingUnavailableAsync"/>), or an
/// import fetched a list the check refused (<see cref="OpsEventService.RecordCpeMappingListRefusedAsync"/>).
/// Every instance runs into the same outage, so each of them reads today's Maintenance events
/// first and records one event per UTC day, cause (for a refusal: list) and severity.
/// </summary>
public class CpeMappingOpsEventTests
{
    private const string UnreadableWithoutACopy = "CPE mappings could not be loaded; vulnerability scans are postponed";
    private const string UnreadableWithACopy = "CPE mappings could not be reloaded; an earlier copy keeps serving";

    private sealed class Rig
    {
        public Rig()
        {
            var repository = new Mock<IOpsEventRepository>();
            repository.Setup(r => r.SaveOpsEventAsync(It.IsAny<OpsEventEntry>()))
                .Returns<OpsEventEntry>(entry =>
                {
                    Recorded.Add(entry);
                    return Task.CompletedTask;
                });
            repository.Setup(r => r.GetOpsEventsAsync(
                    It.IsAny<string?>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<OpsEventQueryFilters?>()))
                .Returns<string?, DateTime?, DateTime?, OpsEventQueryFilters?>((category, from, to, _) =>
                {
                    Reads.Add((category, from, to));
                    if (ReadFailure != null) return Task.FromException<List<OpsEventEntry>>(ReadFailure);
                    return Task.FromResult(AlreadyInTheTable.Concat(Recorded)
                        .Where(e => (category == null || e.Category == category)
                            && (from == null || e.Timestamp >= from)
                            && (to == null || e.Timestamp < to))
                        .OrderByDescending(e => e.Timestamp)
                        .ToList());
                });

            var adminConfig = new Mock<AdminConfigurationService>(
                Mock.Of<IConfigRepository>(), NullLogger<AdminConfigurationService>.Instance,
                new MemoryCache(new MemoryCacheOptions())) { CallBase = false };
            Service = new OpsEventService(
                repository.Object, NullLogger<OpsEventService>.Instance, TestNotifications.InertOpsAlertDispatch(adminConfig.Object));
        }

        public OpsEventService Service { get; }

        /// <summary>The events the service recorded, in order.</summary>
        public List<OpsEventEntry> Recorded { get; } = new();

        /// <summary>Events another instance, or an earlier day, left in the table.</summary>
        public List<OpsEventEntry> AlreadyInTheTable { get; } = new();

        /// <summary>Every read of the events: the category and the window that were asked for.</summary>
        public List<(string? Category, DateTime? From, DateTime? To)> Reads { get; } = new();

        /// <summary>When set, reading the events fails with it.</summary>
        public Exception? ReadFailure { get; set; }

        public IEnumerable<(string Severity, string Message)> RecordedAs => Recorded.Select(e => (e.Severity, e.Message));
    }

    private static OpsEventEntry Stored(string eventType, string severity, string message, DateTime timestampUtc) => new()
    {
        Category = OpsEventCategory.Maintenance,
        EventType = eventType,
        Severity = severity,
        Message = message,
        Timestamp = timestampUtc,
    };

    // -----------------------------------------------------------------------
    // The mapping table could not be read
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Unreadable_WithoutAnEarlierCopy_IsAnError_BecauseScansWait()
    {
        var rig = new Rig();

        await rig.Service.RecordCpeMappingUnavailableAsync("RequestFailedException: storage down", servingEarlierCopy: false);

        var recorded = Assert.Single(rig.Recorded);
        Assert.Equal(OpsEventCategory.Maintenance, recorded.Category);
        Assert.Equal(OpsEventTypes.CpeMappingUnavailable, recorded.EventType);
        Assert.Equal(OpsEventSeverity.Error, recorded.Severity);
        Assert.Equal(UnreadableWithoutACopy, recorded.Message);
        Assert.Null(recorded.TenantId);
        using var details = JsonDocument.Parse(recorded.Details!);
        Assert.Equal("RequestFailedException: storage down", details.RootElement.GetProperty("error").GetString());
        Assert.False(details.RootElement.GetProperty("servingEarlierCopy").GetBoolean());
    }

    [Fact]
    public async Task Unreadable_WhileAnEarlierCopyServes_IsAWarning()
    {
        var rig = new Rig();

        await rig.Service.RecordCpeMappingUnavailableAsync("RequestFailedException: storage down", servingEarlierCopy: true);

        var recorded = Assert.Single(rig.Recorded);
        Assert.Equal(OpsEventTypes.CpeMappingUnavailable, recorded.EventType);
        Assert.Equal(OpsEventSeverity.Warning, recorded.Severity);
        Assert.Equal(UnreadableWithACopy, recorded.Message);
        using var details = JsonDocument.Parse(recorded.Details!);
        Assert.True(details.RootElement.GetProperty("servingEarlierCopy").GetBoolean());
    }

    [Fact]
    public async Task Unreadable_ReadsTodaysMaintenanceEventsBeforeItRecords()
    {
        var rig = new Rig();
        var dayAtTheStart = DateTime.UtcNow.Date;

        await rig.Service.RecordCpeMappingUnavailableAsync("storage down", servingEarlierCopy: false);

        var read = Assert.Single(rig.Reads);
        Assert.Equal(OpsEventCategory.Maintenance, read.Category);
        // The day as it was when the call ran, which is the next one only across midnight.
        Assert.InRange(read.From!.Value, dayAtTheStart, DateTime.UtcNow.Date);
        Assert.Equal(TimeSpan.Zero, read.From.Value.TimeOfDay);
        Assert.Equal(read.From.Value.AddDays(1), read.To);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Unreadable_ASecondTimeOnTheSameDay_RecordsNothing(bool servingEarlierCopy)
    {
        var rig = new Rig();

        await rig.Service.RecordCpeMappingUnavailableAsync("storage down", servingEarlierCopy);
        await rig.Service.RecordCpeMappingUnavailableAsync("storage down again", servingEarlierCopy);
        await rig.Service.RecordCpeMappingUnavailableAsync("and again", servingEarlierCopy);

        Assert.Single(rig.Recorded);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Unreadable_WhenAnotherInstanceRecordedItToday_RecordsNothing(bool servingEarlierCopy)
    {
        var rig = new Rig();
        rig.AlreadyInTheTable.Add(Stored(
            OpsEventTypes.CpeMappingUnavailable,
            servingEarlierCopy ? OpsEventSeverity.Warning : OpsEventSeverity.Error,
            servingEarlierCopy ? UnreadableWithACopy : UnreadableWithoutACopy,
            DateTime.UtcNow));

        await rig.Service.RecordCpeMappingUnavailableAsync("storage down", servingEarlierCopy);

        Assert.Empty(rig.Recorded);
    }

    [Fact]
    public async Task Unreadable_AnErrorAfterAWarning_IsRecorded()
    {
        var rig = new Rig();

        await rig.Service.RecordCpeMappingUnavailableAsync("storage down", servingEarlierCopy: true);
        // An instance without a copy cannot scan at all: that is worse news than the warning was.
        await rig.Service.RecordCpeMappingUnavailableAsync("storage down", servingEarlierCopy: false);
        await rig.Service.RecordCpeMappingUnavailableAsync("storage down", servingEarlierCopy: true);
        await rig.Service.RecordCpeMappingUnavailableAsync("storage down", servingEarlierCopy: false);

        Assert.Equal(
            new[] { (OpsEventSeverity.Warning, UnreadableWithACopy), (OpsEventSeverity.Error, UnreadableWithoutACopy) },
            rig.RecordedAs);
    }

    [Fact]
    public async Task Unreadable_AWarningAfterAnError_IsNotRecorded()
    {
        var rig = new Rig();

        await rig.Service.RecordCpeMappingUnavailableAsync("storage down", servingEarlierCopy: false);
        await rig.Service.RecordCpeMappingUnavailableAsync("storage down", servingEarlierCopy: true);

        Assert.Equal(new[] { (OpsEventSeverity.Error, UnreadableWithoutACopy) }, rig.RecordedAs);
    }

    [Fact]
    public async Task Unreadable_OnTheDayAfterAnEvent_IsRecordedAgain()
    {
        var rig = new Rig();
        rig.AlreadyInTheTable.Add(Stored(
            OpsEventTypes.CpeMappingUnavailable, OpsEventSeverity.Error, UnreadableWithoutACopy, DateTime.UtcNow.Date.AddMinutes(-1)));

        await rig.Service.RecordCpeMappingUnavailableAsync("storage down", servingEarlierCopy: false);

        Assert.Single(rig.Recorded);
    }

    [Fact]
    public async Task NeitherEvent_IsHeldBackByOtherMaintenanceEventsOfTheDay()
    {
        var rig = new Rig();
        // The night's run already said that its sync fell short, for this very reason.
        rig.AlreadyInTheTable.Add(Stored(
            OpsEventTypes.VulnerabilityDataSyncDegraded, OpsEventSeverity.Warning,
            "Vulnerability data sync degraded: automatic mappings or inventory reconcile failed", DateTime.UtcNow));

        await rig.Service.RecordCpeMappingUnavailableAsync("storage down", servingEarlierCopy: false);
        await rig.Service.RecordCpeMappingListRefusedAsync("seed", new[] { "the list holds no mapping" });

        Assert.Equal(2, rig.Recorded.Count);
    }

    // -----------------------------------------------------------------------
    // A list the check refused
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RefusedList_IsAnError_ThatNamesTheFirstProblem_AndCarriesUpToTwentyOfThem()
    {
        var rig = new Rig();
        var problems = Enumerable.Range(1, 25).Select(n => $"mapping {n} (contoso:contoso tool {n}): normalizedProduct is missing").ToList();

        await rig.Service.RecordCpeMappingListRefusedAsync("seed", problems);

        var recorded = Assert.Single(rig.Recorded);
        Assert.Equal(OpsEventCategory.Maintenance, recorded.Category);
        Assert.Equal(OpsEventTypes.CpeMappingUnavailable, recorded.EventType);
        Assert.Equal(OpsEventSeverity.Error, recorded.Severity);
        Assert.Equal($"CPE mapping list refused (seed), the mappings in service were kept: {problems[0]}", recorded.Message);
        using var details = JsonDocument.Parse(recorded.Details!);
        Assert.Equal("seed", details.RootElement.GetProperty("list").GetString());
        Assert.Equal(problems.Take(20), details.RootElement.GetProperty("problems").EnumerateArray().Select(p => p.GetString()));
    }

    [Fact]
    public async Task RefusedList_ASecondTimeOnTheSameDay_RecordsNothing()
    {
        var rig = new Rig();

        await rig.Service.RecordCpeMappingListRefusedAsync("seed", new[] { "the list holds no mapping" });
        // Another import of the same list, turned down for another reason: still the same news for the day.
        await rig.Service.RecordCpeMappingListRefusedAsync("seed", new[] { "not valid JSON: unexpected end of data" });

        Assert.Contains("the list holds no mapping", Assert.Single(rig.Recorded).Message);
    }

    [Fact]
    public async Task RefusedList_TheOtherListOnTheSameDay_IsItsOwnEvent()
    {
        var rig = new Rig();

        await rig.Service.RecordCpeMappingListRefusedAsync("community", new[] { "not valid JSON: unexpected end of data" });
        await rig.Service.RecordCpeMappingListRefusedAsync("seed", new[] { "the list holds no mapping" });

        // One reseed imports both lists; the second refusal must not disappear behind the first.
        Assert.Equal(
            new[]
            {
                "CPE mapping list refused (community), the mappings in service were kept: not valid JSON: unexpected end of data",
                "CPE mapping list refused (seed), the mappings in service were kept: the list holds no mapping",
            },
            rig.Recorded.Select(e => e.Message));
    }

    [Fact]
    public async Task RefusedList_WithoutAProblem_RecordsNothing()
    {
        var rig = new Rig();

        await rig.Service.RecordCpeMappingListRefusedAsync("seed", Array.Empty<string>());

        Assert.Empty(rig.Recorded);
    }

    // -----------------------------------------------------------------------
    // The two causes next to each other
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RefusedListAndUnreadableTable_DoNotHoldEachOtherBack(bool refusedFirst)
    {
        var rig = new Rig();
        Task Refused() => rig.Service.RecordCpeMappingListRefusedAsync("seed", new[] { "the list holds no mapping" });
        Task Unreadable() => rig.Service.RecordCpeMappingUnavailableAsync("storage down", servingEarlierCopy: false);

        await (refusedFirst ? Refused() : Unreadable());
        await (refusedFirst ? Unreadable() : Refused());

        Assert.Equal(2, rig.Recorded.Count);
        Assert.Single(rig.Recorded, e => e.Message == UnreadableWithoutACopy);
        Assert.Single(rig.Recorded, e => e.Message.StartsWith("CPE mapping list refused", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RefusedList_DoesNotHoldBackTheWarningOfAnUnreadableTable_ThoughItIsTheMoreSevereEvent()
    {
        var rig = new Rig();

        await rig.Service.RecordCpeMappingListRefusedAsync("seed", new[] { "the list holds no mapping" });
        await rig.Service.RecordCpeMappingUnavailableAsync("storage down", servingEarlierCopy: true);

        Assert.Equal(new[] { OpsEventSeverity.Error, OpsEventSeverity.Warning }, rig.Recorded.Select(e => e.Severity));
    }

    [Fact]
    public async Task TodaysEventsThatCannotBeRead_AreNoReasonToStaySilent()
    {
        var rig = new Rig { ReadFailure = new InvalidOperationException("table storage is not reachable") };

        await rig.Service.RecordCpeMappingUnavailableAsync("storage down", servingEarlierCopy: false);
        await rig.Service.RecordCpeMappingUnavailableAsync("storage down", servingEarlierCopy: false);
        await rig.Service.RecordCpeMappingListRefusedAsync("seed", new[] { "the list holds no mapping" });

        // One event too many is the smaller harm.
        Assert.Equal(3, rig.Recorded.Count);
    }
}
