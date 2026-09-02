using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Optional <c>fields=</c> projection for raw-event reader endpoints. Lets callers request a lean
/// subset and drop the heavy <see cref="EnrollmentEvent.Data"/> payload for counting/aggregation.
/// Projection is presentation-only — it does not participate in continuation fingerprints.
/// </summary>
public class EventFieldProjectionTests
{
    private static EnrollmentEvent SampleEvent() => new()
    {
        EventId = "evt-1",
        SessionId = "sess-1",
        TenantId = "contoso-tenant-id",
        EventType = "app_install_failed",
        Severity = EventSeverity.Error,
        Source = "ImeLogTracker",
        Message = "install failed",
        Sequence = 42,
        Data = new Dictionary<string, object> { ["errorCode"] = "0x87d1041c", ["bigBlob"] = new string('x', 1000) },
    };

    [Fact]
    public void Project_NullOrEmptyFields_ReturnsEventsVerbatim()
    {
        var e = SampleEvent();
        var events = new List<EnrollmentEvent> { e };

        Assert.Same(e, Assert.Single(EventFieldProjection.Project(events, null)));
        Assert.Same(e, Assert.Single(EventFieldProjection.Project(events, "")));
        Assert.Same(e, Assert.Single(EventFieldProjection.Project(events, "   ")));
    }

    [Fact]
    public void Project_ExplicitSubset_ReturnsOnlyRequestedKeys()
    {
        var result = EventFieldProjection.Project(new[] { SampleEvent() }, "eventType,severity,timestamp");
        var dict = Assert.IsType<Dictionary<string, object?>>(Assert.Single(result));

        Assert.Equal(new[] { "eventType", "severity", "timestamp" }.OrderBy(k => k),
            dict.Keys.OrderBy(k => k));
        Assert.Equal("app_install_failed", dict["eventType"]);
        Assert.Equal("Error", dict["severity"]);
        Assert.False(dict.ContainsKey("data"));
    }

    [Fact]
    public void Project_FieldsAreCaseInsensitive()
    {
        var result = EventFieldProjection.Project(new[] { SampleEvent() }, "EventType, Severity");
        var dict = Assert.IsType<Dictionary<string, object?>>(Assert.Single(result));

        Assert.Equal("app_install_failed", dict["eventType"]);
        Assert.Equal("Error", dict["severity"]);
    }

    [Fact]
    public void Project_UnknownOnlyFields_FallBackToDefaultSubset()
    {
        var result = EventFieldProjection.Project(new[] { SampleEvent() }, "totallyBogus,alsoBogus");
        var dict = Assert.IsType<Dictionary<string, object?>>(Assert.Single(result));

        var expectedDefault = new[] { "eventType", "severity", "source", "timestamp", "message", "sequence" };
        Assert.Equal(expectedDefault.OrderBy(k => k), dict.Keys.OrderBy(k => k));
        Assert.False(dict.ContainsKey("data"));
    }

    [Fact]
    public void Project_DataExcludedByDefault_PresentWhenRequested()
    {
        var withoutData = EventFieldProjection.Project(new[] { SampleEvent() }, "eventType");
        Assert.False(Assert.IsType<Dictionary<string, object?>>(Assert.Single(withoutData)).ContainsKey("data"));

        var withData = EventFieldProjection.Project(new[] { SampleEvent() }, "eventType,data");
        var dict = Assert.IsType<Dictionary<string, object?>>(Assert.Single(withData));
        Assert.True(dict.ContainsKey("data"));
        Assert.IsType<Dictionary<string, object>>(dict["data"]);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("eventType,severity", false)]
    [InlineData("eventType,data", true)]
    [InlineData("DATA", true)]
    public void WantsData_ReflectsProjection(string? fields, bool expected)
    {
        Assert.Equal(expected, EventFieldProjection.WantsData(fields));
    }

    [Fact]
    public void Project_DataSubKeys_ReturnSliceOfPayload()
    {
        var e = SampleEvent();
        e.Data!["scriptType"] = "remediation";
        e.Data["result"] = "Failed";

        var result = EventFieldProjection.Project(new[] { e }, "eventType,data.scriptType,data.RESULT,data.missing");
        var dict = Assert.IsType<Dictionary<string, object?>>(Assert.Single(result));

        Assert.Equal(new[] { "data", "eventType" }, dict.Keys.OrderBy(k => k));
        var data = Assert.IsType<Dictionary<string, object>>(dict["data"]);
        Assert.Equal(new[] { "result", "scriptType" }, data.Keys.OrderBy(k => k));
        Assert.Equal("remediation", data["scriptType"]);
        Assert.Equal("Failed", data["result"]);
        Assert.False(data.ContainsKey("bigBlob"));
    }

    [Fact]
    public void Project_DataSubKeys_NoMatchYieldsEmptyObject_NullPayloadStaysNull()
    {
        var noMatch = EventFieldProjection.Project(new[] { SampleEvent() }, "eventType,data.nope");
        var noMatchData = Assert.IsType<Dictionary<string, object>>(
            Assert.IsType<Dictionary<string, object?>>(Assert.Single(noMatch))["data"]);
        Assert.Empty(noMatchData);

        var nullPayload = SampleEvent();
        nullPayload.Data = null!;
        var projected = EventFieldProjection.Project(new[] { nullPayload }, "eventType,data.scriptType");
        var dict = Assert.IsType<Dictionary<string, object?>>(Assert.Single(projected));
        Assert.True(dict.ContainsKey("data"));
        Assert.Null(dict["data"]);
    }

    [Fact]
    public void Project_BareDataWinsOverSubKeys()
    {
        var result = EventFieldProjection.Project(new[] { SampleEvent() }, "data,data.errorCode");
        var data = Assert.IsType<Dictionary<string, object>>(
            Assert.IsType<Dictionary<string, object?>>(Assert.Single(result))["data"]);
        Assert.True(data.ContainsKey("bigBlob"));
    }

    [Fact]
    public void Project_OnlyDataSubKeys_IsNotTreatedAsUnknownFields()
    {
        // A projection consisting solely of data.<key> entries must not fall back to the default subset.
        var result = EventFieldProjection.Project(new[] { SampleEvent() }, "data.errorCode");
        var dict = Assert.IsType<Dictionary<string, object?>>(Assert.Single(result));
        Assert.Equal(new[] { "data" }, dict.Keys);
    }

    [Theory]
    [InlineData("eventType,data.scriptType", true)]
    [InlineData("DATA.result", true)]
    [InlineData("data.", false)]
    public void WantsData_TrueForSubKeys(string fields, bool expected)
    {
        Assert.Equal(expected, EventFieldProjection.WantsData(fields));
    }
}
