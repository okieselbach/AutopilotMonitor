using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.DataAccess.TableStorage;
using AutopilotMonitor.Functions.Functions.Config;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// D-285: the admin configuration row is changed only through the changed-columns write — fresh
/// read, mutation on a copy, only the columns whose value changed are laid onto the freshly read
/// row, conditional on its ETag. Pins the 2026-09-25 incident: a Global Settings page loaded before
/// an agent release saved one toggle and reverted the release's hashes with the full-model PUT.
/// </summary>
public class AdminConfigurationChangedFieldsTests
{
    private const string ReleaseVersion = "2.0.1465";
    private const string ReleaseZipSha = "9f0b5403da7e49202274fdf8f38675bc5cfea85f7d881e85d5fe4bd71308250e";
    private const string ReleaseExeSha = "503dd02b3c3690acfe20c44dfce82c367ca1e56dcedaba3ecc91ff6ce893d726";

    /// <summary>
    /// A one-row table that behaves like storage: reads hand out copies with the current ETag,
    /// a replace with a stale ETag fails 412, a create on an existing row fails 409.
    /// </summary>
    private sealed class FakeAdminTable
    {
        private int _version = 1;
        public TableEntity? Current;
        public int Writes;
        public TableUpdateMode? LastMode;
        /// <summary>Runs once, right before the first replace is checked — a concurrent writer.</summary>
        public Action<FakeAdminTable>? BeforeFirstReplace;
        public TableConfigRepository Sut { get; }

        public FakeAdminTable(TableEntity? current)
        {
            Current = current;
            var table = new Mock<TableClient>();
            table.Setup(c => c.GetEntityAsync<TableEntity>(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
                .Returns(() => Current == null
                    ? throw new RequestFailedException(404, "Not Found", "ResourceNotFound", null)
                    : Task.FromResult(Response.FromValue(Copy(Current, _version), Mock.Of<Response>())));
            table.Setup(c => c.UpdateEntityAsync(
                    It.IsAny<TableEntity>(), It.IsAny<ETag>(), It.IsAny<TableUpdateMode>(), It.IsAny<CancellationToken>()))
                .Returns<TableEntity, ETag, TableUpdateMode, CancellationToken>((e, ifMatch, mode, _) =>
                {
                    if (BeforeFirstReplace != null)
                    {
                        var concurrent = BeforeFirstReplace;
                        BeforeFirstReplace = null;
                        concurrent(this);
                    }
                    if (ifMatch != Etag(_version))
                        throw new RequestFailedException(412, "Precondition Failed", "UpdateConditionNotSatisfied", null);
                    Current = Copy(e, _version);
                    _version++;
                    Writes++;
                    LastMode = mode;
                    return Task.FromResult(Mock.Of<Response>());
                });
            table.Setup(c => c.AddEntityAsync(It.IsAny<TableEntity>(), It.IsAny<CancellationToken>()))
                .Returns<TableEntity, CancellationToken>((e, _) =>
                {
                    if (Current != null)
                        throw new RequestFailedException(409, "Conflict", "EntityAlreadyExists", null);
                    Current = Copy(e, _version);
                    Writes++;
                    return Task.FromResult(Mock.Of<Response>());
                });

            var serviceClient = new Mock<TableServiceClient>();
            serviceClient.Setup(c => c.GetTableClient(It.IsAny<string>())).Returns(table.Object);
            Sut = new TableConfigRepository(
                new TableStorageService(serviceClient.Object, NullLogger<TableStorageService>.Instance),
                new Mock<IConfigBackupRepository>().Object,
                NullLogger<TableConfigRepository>.Instance);
        }

        /// <summary>A write by someone else (the release pipeline's Merge), bumping the ETag.</summary>
        public void ExternalMerge(string column, object value)
        {
            Current![column] = value;
            _version++;
        }

        private static ETag Etag(int version) => new($"W/\"v{version}\"");

        private static TableEntity Copy(TableEntity source, int version)
            => new(source.Where(kv => kv.Key != "odata.etag").ToDictionary(kv => kv.Key, kv => kv.Value)) { ETag = Etag(version) };
    }

    /// <summary>The stored row right after the agent release: the pipeline's hashes plus a column this build does not know.</summary>
    private static TableEntity ReleasedRow()
    {
        var row = TableConfigRepository.ConvertToAdminTableEntity(new AdminConfiguration
        {
            UpdatedBy = "ga@operator.example",
            GlobalRateLimitRequestsPerMinute = 100,
            NvdApiKey = "stored-key",
            LatestAgentV2Version = ReleaseVersion,
            LatestAgentV2Sha256 = ReleaseZipSha,
            LatestAgentV2ExeSha256 = ReleaseExeSha,
        });
        row["ColumnOfANewerBuild"] = "keep-me";
        return row;
    }

    [Fact]
    public async Task Stale_page_toggle_writes_only_that_field_and_keeps_the_release_hashes()
    {
        var table = new FakeAdminTable(ReleasedRow());
        // The page was loaded before the release; it sends only what the operator changed.
        var fields = JObject.Parse("{ \"enforceClientAppBinding\": true }");
        Assert.Null(PatchAdminConfigurationFunction.CheckFields(fields));

        var result = await table.Sut.UpdateAdminConfigurationAsync(
            draft => PatchAdminConfigurationFunction.ApplyFields(draft, fields), "ga@operator.example");

        Assert.Null(result.Error);
        Assert.Equal(new[] { "EnforceClientAppBinding" }, result.ChangedColumns);
        var row = table.Current!;
        Assert.True(row.GetBoolean("EnforceClientAppBinding"));
        Assert.Equal(ReleaseVersion, row.GetString("LatestAgentV2Version"));
        Assert.Equal(ReleaseZipSha, row.GetString("LatestAgentV2Sha256"));
        Assert.Equal(ReleaseExeSha, row.GetString("LatestAgentV2ExeSha256"));
        Assert.Equal("keep-me", row.GetString("ColumnOfANewerBuild"));
        Assert.Equal("stored-key", row.GetString("NvdApiKey"));
        Assert.Equal(TableUpdateMode.Replace, table.LastMode);
        Assert.Equal(1, table.Writes);
    }

    [Fact]
    public async Task A_concurrent_pipeline_write_is_reread_and_kept()
    {
        var table = new FakeAdminTable(ReleasedRow())
        {
            // The release pipeline lands between the backend's read and its write.
            BeforeFirstReplace = t => t.ExternalMerge("LatestAgentV2Version", "2.0.1466"),
        };

        var result = await table.Sut.UpdateAdminConfigurationAsync(
            c => { c.GlobalRateLimitRequestsPerMinute = 250; return null; }, "System (test)");

        Assert.Null(result.Error);
        Assert.Equal("2.0.1466", table.Current!.GetString("LatestAgentV2Version"));
        Assert.Equal(250, table.Current.GetInt32("GlobalRateLimitRequestsPerMinute"));
        Assert.Equal(1, table.Writes);
    }

    [Fact]
    public async Task A_mutation_that_changes_nothing_writes_nothing()
    {
        var table = new FakeAdminTable(ReleasedRow());

        var result = await table.Sut.UpdateAdminConfigurationAsync(
            c => { c.GlobalRateLimitRequestsPerMinute = 100; return null; }, "ga@operator.example");

        Assert.Null(result.Error);
        Assert.Empty(result.ChangedColumns);
        Assert.Equal(0, table.Writes);
    }

    [Fact]
    public async Task A_rejected_mutation_writes_nothing()
    {
        var table = new FakeAdminTable(ReleasedRow());

        var result = await table.Sut.UpdateAdminConfigurationAsync(
            c => { c.GlobalRateLimitRequestsPerMinute = 999; return "nope"; }, "ga@operator.example");

        Assert.Equal("nope", result.Error);
        Assert.Equal(0, table.Writes);
        Assert.Equal(100, table.Current!.GetInt32("GlobalRateLimitRequestsPerMinute"));
    }

    [Fact]
    public async Task A_null_value_drops_its_column()
    {
        var table = new FakeAdminTable(ReleasedRow());

        var result = await table.Sut.UpdateAdminConfigurationAsync(
            c => { c.NvdApiKey = null!; return null; }, "ga@operator.example"); // what a JSON null in a patch does

        Assert.Equal(new[] { "NvdApiKey" }, result.ChangedColumns);
        Assert.False(table.Current!.ContainsKey("NvdApiKey"));
        Assert.Equal(ReleaseExeSha, table.Current.GetString("LatestAgentV2ExeSha256"));
    }

    [Fact]
    public async Task Without_a_row_the_first_change_creates_it()
    {
        var table = new FakeAdminTable(current: null);

        var result = await table.Sut.UpdateAdminConfigurationAsync(
            c => { c.EnforceClientAppBinding = true; return null; }, "ga@operator.example");

        Assert.Null(result.Error);
        Assert.True(table.Current!.GetBoolean("EnforceClientAppBinding"));
        Assert.Equal(1, table.Writes);
    }

    [Fact]
    public async Task Default_creation_never_replaces_an_existing_row()
    {
        var table = new FakeAdminTable(ReleasedRow());

        var created = await table.Sut.CreateAdminConfigurationIfMissingAsync(AdminConfiguration.CreateDefault());

        Assert.False(created);
        Assert.Equal(ReleaseVersion, table.Current!.GetString("LatestAgentV2Version"));
    }

    [Theory]
    [InlineData("latestAgentV2Version")]
    [InlineData("LatestAgentV2Sha256")]
    [InlineData("latestAgentV2ExeSha256")]
    [InlineData("latestBootstrapV2ScriptVersion")]
    [InlineData("updatedBy")]
    [InlineData("lastUpdated")]
    [InlineData("planTierDefinitionsJson")]
    [InlineData("msrcLastSyncUtc")]
    public void The_gate_rejects_pipeline_stamp_and_flow_owned_fields(string field)
    {
        var error = PatchAdminConfigurationFunction.CheckFields(new JObject { [field] = "x" });
        Assert.NotNull(error);
        Assert.Contains("not writable", error);
    }

    [Fact]
    public void The_gate_rejects_unknown_fields_and_redacted_placeholders()
    {
        Assert.Contains("Unknown field", PatchAdminConfigurationFunction.CheckFields(new JObject { ["noSuchField"] = 1 }));
        Assert.Contains("placeholder", PatchAdminConfigurationFunction.CheckFields(
            new JObject { ["nvdApiKey"] = Constants.RedactedSecretPlaceholder }));
    }

    [Fact]
    public void Patchable_fields_are_the_persisted_settings()
    {
        var patchable = PatchAdminConfigurationFunction.PatchableFields;
        Assert.Contains("EnforceClientAppBinding", patchable);
        Assert.Contains("McpClientRegistrationEnabled", patchable);
        Assert.Contains("McpAccessPolicy", patchable);
        Assert.Contains("NvdApiKey", patchable);
        Assert.Contains("OpsNotificationChannelsJson", patchable);
        Assert.DoesNotContain("LatestAgentV2ExeSha256", patchable);
        Assert.DoesNotContain("PartitionKey", patchable);
        Assert.DoesNotContain("UpdatedBy", patchable);
    }

    [Fact]
    public void Applying_validates_the_result_without_echoing_values()
    {
        var draft = AdminConfiguration.CreateDefault();
        Assert.Contains("at least 1", PatchAdminConfigurationFunction.ApplyFields(
            draft, JObject.Parse("{ \"globalRateLimitRequestsPerMinute\": 0 }")));

        var typeError = PatchAdminConfigurationFunction.ApplyFields(
            AdminConfiguration.CreateDefault(), JObject.Parse("{ \"collectorIdleTimeoutMinutes\": \"https://secret.example/sas?sig=abc\" }"));
        Assert.NotNull(typeError);
        Assert.DoesNotContain("secret.example", typeError);
    }
}
