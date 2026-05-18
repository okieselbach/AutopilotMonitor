using AutopilotMonitor.Functions.DataAccess.TableStorage;

namespace AutopilotMonitor.Functions.Tests.Offboarding;

/// <summary>
/// PR3.B §3.2 — static-helper tests for the archive RowKey base64url encoding and the
/// PartitionKey construction. These two functions are the only things in the repository
/// that have logic worth pinning at unit level; the rest is straight Azure Tables CRUD.
/// </summary>
public sealed class TableTenantCustomsArchiveRepositoryHelperTests
{
    [Fact]
    public void BuildPartitionKey_Concatenates_TenantId_And_HistoryRowKey()
    {
        var pk = TableTenantCustomsArchiveRepository.BuildPartitionKey(
            "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
            "20260518091523123_aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        Assert.Equal(
            "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee_20260518091523123_aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
            pk);
    }

    [Theory]
    [InlineData("simple-row-id")]
    [InlineData("rowkey/with/slashes")]
    [InlineData("rowkey#with#hash")]
    [InlineData("rowkey?with?question")]
    [InlineData("rowkey\\with\\backslash")]
    [InlineData("a")]
    [InlineData("ab")]
    [InlineData("abc")]
    [InlineData("abcd")]
    [InlineData("unicode-Ümlaut-and-emoji-✓")]
    public void BuildRowKey_RoundTripsForbiddenCharacters(string originalRowKey)
    {
        var archiveRowKey = TableTenantCustomsArchiveRepository.BuildRowKey("GatherRules", originalRowKey);

        // RowKey itself must NOT contain any Azure-Tables-forbidden characters.
        Assert.DoesNotContain('#', archiveRowKey);
        Assert.DoesNotContain('?', archiveRowKey);
        Assert.DoesNotContain('/', archiveRowKey);
        Assert.DoesNotContain('\\', archiveRowKey);

        // Must round-trip cleanly.
        var decoded = TableTenantCustomsArchiveRepository.DecodeOriginalRowKey(archiveRowKey, "GatherRules");
        Assert.Equal(originalRowKey, decoded);
    }

    [Fact]
    public void BuildRowKey_DifferentTablesProduceDifferentKeys()
    {
        var gatherKey = TableTenantCustomsArchiveRepository.BuildRowKey("GatherRules", "my-rule");
        var analyzeKey = TableTenantCustomsArchiveRepository.BuildRowKey("AnalyzeRules", "my-rule");

        Assert.NotEqual(gatherKey, analyzeKey);
        Assert.StartsWith("GatherRules_", gatherKey);
        Assert.StartsWith("AnalyzeRules_", analyzeKey);
    }

    [Fact]
    public void DecodeOriginalRowKey_RejectsPrefixMismatch()
    {
        var rowKey = TableTenantCustomsArchiveRepository.BuildRowKey("GatherRules", "x");
        Assert.Throws<ArgumentException>(() =>
            TableTenantCustomsArchiveRepository.DecodeOriginalRowKey(rowKey, "AnalyzeRules"));
    }
}
