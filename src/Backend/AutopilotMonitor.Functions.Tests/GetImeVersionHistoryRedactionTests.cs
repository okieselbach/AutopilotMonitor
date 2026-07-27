using System.Text.Json;
using AutopilotMonitor.Functions.Functions.Metrics;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Guards the cross-tenant redaction on GET metrics/ime-versions.
///
/// The route is <c>MemberRead</c> (EndpointAccessPolicyCatalog) and the archive itself is a
/// single global partition — every tenant member reads the SAME platform-wide rows. The only
/// thing standing between an ordinary tenant user and another tenant's identifiers is the
/// projection in <see cref="GetImeVersionHistoryFunction.BuildResponsePayload"/>. That made it
/// worth pinning: the redaction had no test at all, and a boundary nobody asserts is one a
/// later refactor can open without a single failure.
///
/// The JSON tests are the ones that matter — a property dropped from an anonymous type is only
/// a real redaction once it is also absent from the wire, and the wire is what the customer's
/// MCP client receives. camelCase mirrors Program.cs's PropertyNamingPolicy.
/// </summary>
public class GetImeVersionHistoryRedactionTests
{
    private static readonly DateTime First = new(2026, 7, 8, 3, 18, 13, DateTimeKind.Utc);
    private static readonly DateTime Last = new(2026, 7, 27, 16, 59, 34, DateTimeKind.Utc);

    private static readonly JsonSerializerOptions CamelCase =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    // =========================================================================
    // Non-global caller (ordinary tenant member) — the redaction
    // =========================================================================

    [Fact]
    public void NonGlobalCaller_DropsForeignTenantAndSessionIds()
    {
        var json = SerializeFor(hasGlobalScope: false, MakeEntry());

        Assert.DoesNotContain("firstSeenTenantId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("firstSeenSessionId", json, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The values, not just the property names: a rename of the model property must not let the
    /// identifier survive under a different key. Asserting on the GUIDs catches that.
    /// </summary>
    [Fact]
    public void NonGlobalCaller_ForeignIdentifierValuesAppearNowhereInThePayload()
    {
        var json = SerializeFor(hasGlobalScope: false, MakeEntry());

        Assert.DoesNotContain("57f34dd1-6a42-47f9-9bc0-b921fa6caa30", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("baae0453-6d19-47b5-aa58-8bed5a572fc8", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NonGlobalCaller_ExposesExactlyTheFourAllowedProperties()
    {
        using var doc = JsonDocument.Parse(SerializeFor(hasGlobalScope: false, MakeEntry()));

        var keys = doc.RootElement[0].EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray();

        Assert.Equal(new[] { "firstSeenAt", "lastSeenAt", "sessionCount", "version" }, keys);
    }

    /// <summary>
    /// SessionCount stays on purpose — it is a platform-wide total, and the route exists so any
    /// tenant member can track Microsoft's IME rollout. Pinned so that "the counts are global"
    /// stays a decision someone made rather than something that quietly drifts either way.
    /// </summary>
    [Fact]
    public void NonGlobalCaller_KeepsVersionDatesAndGlobalSessionCount()
    {
        using var doc = JsonDocument.Parse(SerializeFor(hasGlobalScope: false, MakeEntry()));

        var row = doc.RootElement[0];
        Assert.Equal("1.103.101.0", row.GetProperty("version").GetString());
        Assert.Equal(First, row.GetProperty("firstSeenAt").GetDateTime());
        Assert.Equal(Last, row.GetProperty("lastSeenAt").GetDateTime());
        Assert.Equal(3109, row.GetProperty("sessionCount").GetInt32());
    }

    // =========================================================================
    // Global caller (Global Admin or Global Reader) — full archive
    // =========================================================================

    [Fact]
    public void GlobalCaller_KeepsTenantAndSessionIds()
    {
        using var doc = JsonDocument.Parse(SerializeFor(hasGlobalScope: true, MakeEntry()));

        var row = doc.RootElement[0];
        Assert.Equal("57f34dd1-6a42-47f9-9bc0-b921fa6caa30", row.GetProperty("firstSeenTenantId").GetString());
        Assert.Equal("baae0453-6d19-47b5-aa58-8bed5a572fc8", row.GetProperty("firstSeenSessionId").GetString());
    }

    [Fact]
    public void GlobalCaller_ExposesTheFullEntry()
    {
        using var doc = JsonDocument.Parse(SerializeFor(hasGlobalScope: true, MakeEntry()));

        var keys = doc.RootElement[0].EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray();

        Assert.Equal(
            new[] { "firstSeenAt", "firstSeenSessionId", "firstSeenTenantId", "lastSeenAt", "sessionCount", "version" },
            keys);
    }

    // =========================================================================
    // Shape-preserving properties of the projection
    // =========================================================================

    [Fact]
    public void BothScopes_EmitAJsonArray_EvenForASingleRow()
    {
        foreach (var scope in new[] { true, false })
        {
            using var doc = JsonDocument.Parse(SerializeFor(scope, MakeEntry()));
            Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
            Assert.Equal(1, doc.RootElement.GetArrayLength());
        }
    }

    [Fact]
    public void BothScopes_EmptyArchive_SerializesAsEmptyArray()
    {
        foreach (var scope in new[] { true, false })
        {
            using var doc = JsonDocument.Parse(SerializeFor(scope));
            Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
            Assert.Equal(0, doc.RootElement.GetArrayLength());
        }
    }

    /// <summary>
    /// The repository orders by FirstSeenAt descending; the projection must not reshuffle, or a
    /// redacted caller would see a different sequence than a Global Admin for the same archive.
    /// </summary>
    [Fact]
    public void NonGlobalCaller_PreservesRowOrderAndCount()
    {
        var newest = MakeEntry("1.103.101.0", First);
        var older = MakeEntry("1.101.111.0", First.AddDays(-48));
        var oldest = MakeEntry("1.99.101.0", First.AddDays(-131));

        using var doc = JsonDocument.Parse(SerializeFor(false, newest, older, oldest));

        Assert.Equal(3, doc.RootElement.GetArrayLength());
        Assert.Equal("1.103.101.0", doc.RootElement[0].GetProperty("version").GetString());
        Assert.Equal("1.101.111.0", doc.RootElement[1].GetProperty("version").GetString());
        Assert.Equal("1.99.101.0", doc.RootElement[2].GetProperty("version").GetString());
    }

    /// <summary>
    /// BuildResponsePayload returns `object`; the branch that keeps everything hands back the
    /// entries themselves. System.Text.Json resolves an `object` declared type via the runtime
    /// type, which is what keeps the wire format identical to the pre-extraction inline code —
    /// assert it rather than trust it, since a regression here would silently emit "{}" rows.
    /// </summary>
    [Fact]
    public void GlobalPayload_SerializedAsObject_StillEmitsPopulatedRows()
    {
        var payload = GetImeVersionHistoryFunction.BuildResponsePayload([MakeEntry()], hasGlobalScope: true);

        var json = JsonSerializer.Serialize(payload, CamelCase);

        Assert.Contains("1.103.101.0", json, StringComparison.Ordinal);
        Assert.NotEqual("[{}]", json);
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private static string SerializeFor(bool hasGlobalScope, params ImeVersionHistoryEntry[] versions) =>
        JsonSerializer.Serialize(
            GetImeVersionHistoryFunction.BuildResponsePayload(versions, hasGlobalScope),
            CamelCase);

    private static ImeVersionHistoryEntry MakeEntry(string version = "1.103.101.0", DateTime? firstSeenAt = null) =>
        new()
        {
            Version = version,
            FirstSeenAt = firstSeenAt ?? First,
            FirstSeenSessionId = "baae0453-6d19-47b5-aa58-8bed5a572fc8",
            FirstSeenTenantId = "57f34dd1-6a42-47f9-9bc0-b921fa6caa30",
            LastSeenAt = Last,
            SessionCount = 3109,
        };
}
