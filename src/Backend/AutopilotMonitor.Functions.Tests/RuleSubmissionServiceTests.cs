using System.Text.Json;
using System.Text.Json.Nodes;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Pagination;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// The pure pieces of <see cref="RuleSubmissionService"/> (id suggestion, repo file, snapshot
/// stripping, attribution shape) and the review state machine against mocked repositories.
/// </summary>
public class RuleSubmissionServiceTests
{
    private const string TenantId = "11111111-1111-1111-1111-111111111111";

    // ── id suggestion ───────────────────────────────────────────────────────

    [Fact]
    public void Suggest_is_max_plus_one_and_never_fills_a_gap()
    {
        // 001 and 003 exist, 002 is a retired id — the next is 004, not 002.
        var taken = new[] { "ANALYZE-NET-001", "ANALYZE-NET-003", "ANALYZE-APP-018", "GATHER-NET-009" };
        Assert.Equal("ANALYZE-NET-004", RuleSubmissionService.SuggestPublishedRuleId("analyze", "network", taken));
    }

    [Fact]
    public void Suggest_starts_at_001_for_an_empty_category_and_maps_the_kind_convention()
    {
        Assert.Equal("ANALYZE-NET-001", RuleSubmissionService.SuggestPublishedRuleId("analyze", "network", Array.Empty<string>()));
        Assert.Equal("GATHER-DEVICE-001", RuleSubmissionService.SuggestPublishedRuleId("gather", "device", Array.Empty<string>()));
        Assert.Equal("ANALYZE-DEV-001", RuleSubmissionService.SuggestPublishedRuleId("analyze", "device", Array.Empty<string>()));
        Assert.Equal("ANALYZE-ENRL-001", RuleSubmissionService.SuggestPublishedRuleId("analyze", "enrollment", Array.Empty<string>()));
        Assert.Equal("GATHER-ENROLL-001", RuleSubmissionService.SuggestPublishedRuleId("gather", "enrollment", Array.Empty<string>()));
    }

    [Fact]
    public void Suggest_is_case_insensitive_over_taken_ids()
    {
        Assert.Equal("ANALYZE-SEC-008", RuleSubmissionService.SuggestPublishedRuleId("analyze", "security", new[] { "analyze-sec-007" }));
    }

    [Fact]
    public void Suggest_falls_back_to_the_upper_cased_category_when_unmapped()
    {
        Assert.Equal("ANALYZE-OFFICE-001", RuleSubmissionService.SuggestPublishedRuleId("analyze", "office", Array.Empty<string>()));
    }

    // ── freeze + repo file ──────────────────────────────────────────────────

    private static AnalyzeRule CustomAnalyzeRule() => new()
    {
        RuleId = "ANALYZE-CUSTOM-003",
        Title = "Proxy PAC unreachable",
        Description = "PAC download fails",
        Severity = "warning",
        Category = "network",
        Version = "1.2.0",
        Author = "Alice Admin",
        Enabled = false,
        IsBuiltIn = false,
        IsCommunity = false,
        Provenance = "embedded",
        MarkSessionAsFailed = true,
        Notify = true,
        NotifyChannelIds = new List<string> { "chan-1" },
        DerivedFromTemplateRuleId = "ANALYZE-SEC-004",
        Explanation = "The PAC file could not be fetched.",
        Conditions = new List<RuleCondition>
        {
            new() { Signal = "pac_failed", Source = "event_type", EventType = "network_pac_failed", Required = true },
        },
        CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        UpdatedAt = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    [Fact]
    public void Freeze_strips_server_and_tenant_fields_and_keeps_the_rule()
    {
        var json = RuleSubmissionService.FreezeRule(CustomAnalyzeRule());
        var node = JsonNode.Parse(json)!.AsObject();

        foreach (var stripped in new[] { "isBuiltIn", "provenance", "createdAt", "updatedAt", "markSessionAsFailed", "notifyDefault", "notify", "notifyChannelIds" })
            Assert.False(node.ContainsKey(stripped), $"{stripped} must not be frozen");

        Assert.Equal("ANALYZE-CUSTOM-003", node["ruleId"]!.GetValue<string>());
        Assert.Equal("Alice Admin", node["author"]!.GetValue<string>());
        Assert.Equal("ANALYZE-SEC-004", node["derivedFromTemplateRuleId"]!.GetValue<string>());
        Assert.Single(node["conditions"]!.AsArray());
    }

    [Fact]
    public void RepoFile_carries_schema_pointer_assigned_id_credit_and_community_flag_in_repo_order()
    {
        var s = new RuleSubmission
        {
            RuleKind = RuleSubmissionKinds.Analyze,
            RuleJson = RuleSubmissionService.FreezeRule(CustomAnalyzeRule()),
            AttributionName = RuleAttributionModes.AnonymousAuthor,
        };

        var file = RuleSubmissionService.BuildRepoFile(s, "ANALYZE-NET-002");

        Assert.Equal("rules/analyze/ANALYZE-NET-002.json", file.Path);
        Assert.EndsWith("\n", file.Content);
        var node = JsonNode.Parse(file.Content)!.AsObject();
        var keys = node.Select(kv => kv.Key).ToList();
        Assert.Equal(new[] { "$schema", "ruleId", "title", "description", "severity", "category", "version", "author", "enabled", "isCommunity" }, keys.Take(10));
        Assert.Equal("../schema/analyze-rule.schema.json", node["$schema"]!.GetValue<string>());
        Assert.Equal("ANALYZE-NET-002", node["ruleId"]!.GetValue<string>());
        Assert.Equal("Community contribution", node["author"]!.GetValue<string>());
        Assert.True(node["isCommunity"]!.GetValue<bool>());
        // Analyze rules ship enabled — the tenant's own toggle state does not travel.
        Assert.True(node["enabled"]!.GetValue<bool>());
        Assert.False(node.ContainsKey("derivedFromTemplateRuleId"));
        Assert.False(node.ContainsKey("isBuiltIn"));
        Assert.Contains("\"explanation\": \"The PAC file could not be fetched.\"", file.Content);
    }

    [Fact]
    public void RepoFile_for_a_gather_rule_ships_disabled_and_without_severity()
    {
        var gather = new GatherRule
        {
            RuleId = "GATHER-CUSTOM-001",
            Title = "BIOS config",
            Description = "Reads the BIOS registry block",
            Category = "device",
            Enabled = true,
            IsBuiltIn = false,
            CollectorType = "registry",
            Target = "HKLM\\SOFTWARE\\Vendor\\BIOS",
            Trigger = "startup",
            OutputEventType = "gather_bios_config",
        };
        var s = new RuleSubmission
        {
            RuleKind = RuleSubmissionKinds.Gather,
            RuleJson = RuleSubmissionService.FreezeRule(gather),
            AttributionName = "Contoso IT",
        };

        var file = RuleSubmissionService.BuildRepoFile(s, "GATHER-DEVICE-009");

        Assert.Equal("rules/gather/GATHER-DEVICE-009.json", file.Path);
        var node = JsonNode.Parse(file.Content)!.AsObject();
        Assert.Equal("../schema/gather-rule.schema.json", node["$schema"]!.GetValue<string>());
        Assert.False(node["enabled"]!.GetValue<bool>());
        Assert.Equal("Contoso IT", node["author"]!.GetValue<string>());
        Assert.False(node.ContainsKey("severity"));
        // No \u escaping of the backslash-heavy registry path beyond JSON's own rules.
        Assert.Contains("HKLM\\\\SOFTWARE\\\\Vendor\\\\BIOS", file.Content);
    }

    // ── attribution ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Alice A.")]
    [InlineData("Contoso IT & Ops (EMEA)")]
    [InlineData("Zoë Müller-Lüdenscheidt")]
    public void AttributionName_accepts_names(string name)
        => Assert.Equal(name, RuleSubmissionService.ValidateAttributionName(name));

    [Theory]
    [InlineData("")]
    [InlineData("<script>")]
    [InlineData("https://example.com")]
    [InlineData("line\nbreak")]
    public void AttributionName_rejects_markup_urls_and_control_characters(string name)
        => Assert.Throws<ArgumentException>(() => RuleSubmissionService.ValidateAttributionName(name));

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("  alice@contoso.com  ", "alice@contoso.com")]
    public void Email_is_trimmed_or_null(string? input, string? expected)
        => Assert.Equal(expected, RuleSubmissionService.NormalizeEmail(input));

    [Theory]
    [InlineData("alice")]
    [InlineData("@contoso.com")]
    [InlineData("alice@")]
    [InlineData("a@b@c")]
    [InlineData("alice @contoso.com")]
    public void Email_rejects_non_addresses(string input)
        => Assert.Throws<ArgumentException>(() => RuleSubmissionService.NormalizeEmail(input));

    // ── review state machine ────────────────────────────────────────────────

    private static (RuleSubmissionService service, Mock<IRuleSubmissionRepository> repo) Build(
        RuleSubmission row, IEnumerable<string>? globalAnalyzeIds = null, IEnumerable<string>? reservedIds = null)
    {
        var repo = new Mock<IRuleSubmissionRepository>(MockBehavior.Loose);
        repo.Setup(r => r.GetAsync(row.SubmissionId)).ReturnsAsync(row);
        repo.Setup(r => r.UpdateAsync(It.IsAny<RuleSubmission>())).ReturnsAsync(true);
        repo.Setup(r => r.GetReservedPublishedRuleIdsAsync())
            .ReturnsAsync(new HashSet<string>(reservedIds ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase));

        var rules = new Mock<IRuleRepository>(MockBehavior.Loose);
        rules.Setup(r => r.GetAnalyzeRulesAsync("global")).ReturnsAsync(
            (globalAnalyzeIds ?? Array.Empty<string>()).Select(id => new AnalyzeRule { RuleId = id, IsBuiltIn = true }).ToList());
        rules.Setup(r => r.GetGatherRulesAsync("global")).ReturnsAsync(new List<GatherRule>());

        var metrics = new Mock<IMetricsRepository>(MockBehavior.Loose);
        var config = new Mock<TenantConfigurationService>(MockBehavior.Loose,
            Mock.Of<IConfigRepository>(), NullLogger<TenantConfigurationService>.Instance, new MemoryCache(new MemoryCacheOptions()));

        var service = new RuleSubmissionService(repo.Object, rules.Object, metrics.Object, config.Object,
            NullLogger<RuleSubmissionService>.Instance);
        return (service, repo);
    }

    private static RuleSubmission PendingRow() => new()
    {
        SubmissionId = "a1b2c3d4e5f6",
        BatchId = "b",
        TenantId = TenantId,
        RuleKind = RuleSubmissionKinds.Analyze,
        SourceRuleId = "ANALYZE-CUSTOM-003",
        Title = "Proxy PAC unreachable",
        Category = "network",
        RuleJson = RuleSubmissionService.FreezeRule(CustomAnalyzeRule()),
        SubmittedBy = "alice@contoso.com",
        SubmittedByName = "Alice Admin",
        SubmittedAt = DateTime.UtcNow,
        Status = RuleSubmissionStatuses.Pending,
    };

    [Fact]
    public async Task Approve_requires_a_reserved_id_of_the_same_kind()
    {
        var (service, _) = Build(PendingRow());

        await Assert.ThrowsAsync<ArgumentException>(() => service.ReviewAsync("a1b2c3d4e5f6",
            new ReviewRuleSubmissionRequest { Decision = "approve" }, "ga@example.com"));
        await Assert.ThrowsAsync<ArgumentException>(() => service.ReviewAsync("a1b2c3d4e5f6",
            new ReviewRuleSubmissionRequest { Decision = "approve", PublishedRuleId = "ANALYZE-CUSTOM-009" }, "ga@example.com"));
        await Assert.ThrowsAsync<ArgumentException>(() => service.ReviewAsync("a1b2c3d4e5f6",
            new ReviewRuleSubmissionRequest { Decision = "approve", PublishedRuleId = "GATHER-NET-002" }, "ga@example.com"));
    }

    [Fact]
    public async Task Approve_refuses_an_id_taken_by_the_catalog_or_another_approval()
    {
        var (service, _) = Build(PendingRow(), globalAnalyzeIds: new[] { "ANALYZE-NET-001" }, reservedIds: new[] { "ANALYZE-NET-002" });

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReviewAsync("a1b2c3d4e5f6",
            new ReviewRuleSubmissionRequest { Decision = "approve", PublishedRuleId = "ANALYZE-NET-001" }, "ga@example.com"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReviewAsync("a1b2c3d4e5f6",
            new ReviewRuleSubmissionRequest { Decision = "approve", PublishedRuleId = "ANALYZE-NET-002" }, "ga@example.com"));
    }

    [Fact]
    public async Task Approve_stores_id_comment_adaptation_flag_and_reviewer()
    {
        var (service, repo) = Build(PendingRow());
        RuleSubmission? written = null;
        repo.Setup(r => r.UpdateAsync(It.IsAny<RuleSubmission>())).Callback<RuleSubmission>(s => written = s).ReturnsAsync(true);

        var result = await service.ReviewAsync("a1b2c3d4e5f6",
            new ReviewRuleSubmissionRequest { Decision = "approve", PublishedRuleId = "ANALYZE-NET-003", ReviewComment = "Taken, will generalise the title.", WillBeAdapted = true },
            "ga@example.com");

        Assert.Same(result, written);
        Assert.Equal(RuleSubmissionStatuses.Approved, result.Status);
        Assert.Equal("ANALYZE-NET-003", result.PublishedRuleId);
        Assert.True(result.WillBeAdapted);
        Assert.Equal("Taken, will generalise the title.", result.ReviewComment);
        Assert.Equal("ga@example.com", result.ReviewedBy);
        Assert.NotNull(result.ReviewedAt);
    }

    [Fact]
    public async Task Decline_requires_a_comment_and_clears_the_published_id()
    {
        var row = PendingRow();
        row.Status = RuleSubmissionStatuses.Approved;
        row.PublishedRuleId = "ANALYZE-NET-003";
        var (service, _) = Build(row);

        await Assert.ThrowsAsync<ArgumentException>(() => service.ReviewAsync("a1b2c3d4e5f6",
            new ReviewRuleSubmissionRequest { Decision = "decline" }, "ga@example.com"));

        var result = await service.ReviewAsync("a1b2c3d4e5f6",
            new ReviewRuleSubmissionRequest { Decision = "decline", ReviewComment = "Too tenant-specific." }, "ga@example.com");

        Assert.Equal(RuleSubmissionStatuses.Declined, result.Status);
        Assert.Null(result.PublishedRuleId);
        Assert.False(result.WillBeAdapted);
    }

    [Fact]
    public async Task Published_is_derived_and_final()
    {
        var row = PendingRow();
        row.Status = RuleSubmissionStatuses.Approved;
        row.PublishedRuleId = "ANALYZE-NET-003";
        var (service, _) = Build(row, globalAnalyzeIds: new[] { "ANALYZE-NET-003" });

        var items = await service.ToItemsAsync(new[] { row });
        Assert.Equal(RuleSubmissionStatuses.Published, items[0].Status);
        Assert.Equal(RuleSubmissionStatuses.Approved, row.Status);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReviewAsync("a1b2c3d4e5f6",
            new ReviewRuleSubmissionRequest { Decision = "decline", ReviewComment = "too late" }, "ga@example.com"));
    }

    [Fact]
    public async Task Approved_but_unpublished_stays_approved_on_the_wire()
    {
        var row = PendingRow();
        row.Status = RuleSubmissionStatuses.Approved;
        row.PublishedRuleId = "ANALYZE-NET-003";
        var (service, _) = Build(row, globalAnalyzeIds: new[] { "ANALYZE-NET-001" });

        var items = await service.ToItemsAsync(new[] { row });
        Assert.Equal(RuleSubmissionStatuses.Approved, items[0].Status);
    }

    [Fact]
    public async Task Withdraw_only_pending_and_only_own_tenant()
    {
        var row = PendingRow();
        var (service, _) = Build(row);

        Assert.False(await service.WithdrawAsync("22222222-2222-2222-2222-222222222222", "a1b2c3d4e5f6", crossTenantAllowed: false));
        Assert.True(await service.WithdrawAsync(TenantId, "a1b2c3d4e5f6", crossTenantAllowed: false));
        Assert.Equal(RuleSubmissionStatuses.Withdrawn, row.Status);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.WithdrawAsync(TenantId, "a1b2c3d4e5f6", crossTenantAllowed: false));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReviewAsync("a1b2c3d4e5f6",
            new ReviewRuleSubmissionRequest { Decision = "approve", PublishedRuleId = "ANALYZE-NET-003" }, "ga@example.com"));
    }

    [Fact]
    public async Task Operator_delete_passes_through_to_the_repository()
    {
        var (service, repo) = Build(PendingRow());
        repo.Setup(r => r.DeleteAsync("a1b2c3d4e5f6")).ReturnsAsync(true);

        Assert.True(await service.DeleteAsync("a1b2c3d4e5f6"));
        repo.Verify(r => r.DeleteAsync("a1b2c3d4e5f6"), Times.Once);
    }

    [Fact]
    public async Task Unknown_decision_is_a_bad_request()
    {
        var (service, _) = Build(PendingRow());
        await Assert.ThrowsAsync<ArgumentException>(() => service.ReviewAsync("a1b2c3d4e5f6",
            new ReviewRuleSubmissionRequest { Decision = "maybe" }, "ga@example.com"));
    }

    [Fact]
    public async Task Detail_returns_the_typed_rule_suggested_id_and_repo_file()
    {
        var (service, _) = Build(PendingRow(), globalAnalyzeIds: new[] { "ANALYZE-NET-001" });

        var detail = await service.GetDetailAsync("a1b2c3d4e5f6");

        Assert.NotNull(detail);
        Assert.NotNull(detail!.AnalyzeRule);
        Assert.Null(detail.GatherRule);
        Assert.Equal("ANALYZE-CUSTOM-003", detail.AnalyzeRule!.RuleId);
        Assert.False(detail.AnalyzeRule.IsBuiltIn);
        Assert.Equal("ANALYZE-NET-002", detail.SuggestedPublishedRuleId);
        Assert.Equal("rules/analyze/ANALYZE-NET-002.json", detail.RepoFile!.Path);
    }

    // ── submit ──────────────────────────────────────────────────────────────

    private static (RuleSubmissionService service, List<RuleSubmission> stored) BuildForSubmit(
        List<AnalyzeRule> tenantAnalyze, List<RuleSubmission>? existing = null, TenantConfiguration? config = null)
    {
        var stored = new List<RuleSubmission>();
        var repo = new Mock<IRuleSubmissionRepository>(MockBehavior.Loose);
        repo.Setup(r => r.GetForTenantAsync(TenantId)).ReturnsAsync(existing ?? new List<RuleSubmission>());
        repo.Setup(r => r.AddAsync(It.IsAny<RuleSubmission>())).Callback<RuleSubmission>(stored.Add).ReturnsAsync(true);
        repo.Setup(r => r.GetReservedPublishedRuleIdsAsync()).ReturnsAsync(new HashSet<string>());

        var rules = new Mock<IRuleRepository>(MockBehavior.Loose);
        rules.Setup(r => r.GetAnalyzeRulesAsync(TenantId)).ReturnsAsync(tenantAnalyze);
        rules.Setup(r => r.GetGatherRulesAsync(TenantId)).ReturnsAsync(new List<GatherRule>());
        rules.Setup(r => r.GetAnalyzeRulesAsync("global")).ReturnsAsync(new List<AnalyzeRule>());
        rules.Setup(r => r.GetGatherRulesAsync("global")).ReturnsAsync(new List<GatherRule>());

        var metrics = new Mock<IMetricsRepository>(MockBehavior.Loose);
        metrics.Setup(m => m.GetRuleStatsAsync(TenantId, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>()))
            .ReturnsAsync(new List<RuleStatsEntry>
            {
                new() { RuleId = "ANALYZE-CUSTOM-003", FireCount = 4, SessionsEvaluated = 40, EvaluationCount = 41 },
                new() { RuleId = "ANALYZE-CUSTOM-003", FireCount = 1, SessionsEvaluated = 10, EvaluationCount = 10 },
                new() { RuleId = "ANALYZE-CUSTOM-999", FireCount = 99, SessionsEvaluated = 99, EvaluationCount = 99 },
            });

        var configRepo = new Mock<IConfigRepository>(MockBehavior.Loose);
        var configService = new Mock<TenantConfigurationService>(MockBehavior.Loose, configRepo.Object,
            NullLogger<TenantConfigurationService>.Instance, new MemoryCache(new MemoryCacheOptions()));
        configService.Setup(c => c.GetConfigurationIfExistsAsync(TenantId)).ReturnsAsync(config);

        var service = new RuleSubmissionService(repo.Object, rules.Object, metrics.Object, configService.Object,
            NullLogger<RuleSubmissionService>.Instance);
        return (service, stored);
    }

    private static SubmitRuleSubmissionsRequest Request(params string[] ruleIds) => new()
    {
        TenantId = TenantId,
        Items = ruleIds.Select(id => new RuleSubmissionItemRef { Kind = "analyze", RuleId = id }).ToList(),
        Comment = "Fires when the PAC download fails during ESP.",
        AttributionMode = RuleAttributionModes.Anonymous,
    };

    [Fact]
    public async Task Submit_freezes_the_tenant_rule_records_findings_and_fire_stats()
    {
        var (service, stored) = BuildForSubmit(new List<AnalyzeRule> { CustomAnalyzeRule() });

        var created = await service.SubmitAsync(TenantId, Request("ANALYZE-CUSTOM-003"), "alice@contoso.com", "Alice Admin");

        var s = Assert.Single(created);
        Assert.Same(s, Assert.Single(stored));
        Assert.Equal(12, s.SubmissionId.Length);
        Assert.Equal(12, s.BatchId.Length);
        Assert.Equal(RuleSubmissionStatuses.Pending, s.Status);
        Assert.Equal("Community contribution", s.AttributionName);
        Assert.Null(s.ContactEmail);
        Assert.Equal("Proxy PAC unreachable", s.Title);
        Assert.Equal("network", s.Category);
        Assert.Equal("ANALYZE-SEC-004", s.DerivedFromTemplateRuleId);
        Assert.Contains(s.ValidationFindings, f => f.Level == "info" && f.Message.Contains("ANALYZE-SEC-004"));
        Assert.DoesNotContain(s.ValidationFindings, f => f.Level == "error");
        Assert.Equal(5, s.SourceFireStats!.FireCount);
        Assert.Equal(50, s.SourceFireStats.SessionsEvaluated);
        Assert.DoesNotContain("isBuiltIn", s.RuleJson);
    }

    [Fact]
    public async Task Submit_rejects_built_in_and_unknown_rules_without_storing_anything()
    {
        var builtIn = CustomAnalyzeRule();
        builtIn.RuleId = "ANALYZE-NET-001";
        builtIn.IsBuiltIn = true;
        var (service, stored) = BuildForSubmit(new List<AnalyzeRule> { CustomAnalyzeRule(), builtIn });

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.SubmitAsync(TenantId, Request("ANALYZE-CUSTOM-003", "ANALYZE-NET-001", "ANALYZE-CUSTOM-404"), "alice@contoso.com", "Alice Admin"));

        Assert.Contains("ANALYZE-NET-001", ex.Message);
        Assert.Contains("ANALYZE-CUSTOM-404", ex.Message);
        Assert.Empty(stored);
    }

    [Fact]
    public async Task Submit_rejects_a_rule_whose_draft_validation_fails()
    {
        var broken = CustomAnalyzeRule();
        broken.Conditions = new List<RuleCondition>();
        var (service, stored) = BuildForSubmit(new List<AnalyzeRule> { broken });

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.SubmitAsync(TenantId, Request("ANALYZE-CUSTOM-003"), "alice@contoso.com", "Alice Admin"));

        Assert.Contains("at least one condition", ex.Message);
        Assert.Empty(stored);
    }

    [Fact]
    public async Task Submit_refuses_a_second_pending_submission_of_the_same_rule()
    {
        var (service, _) = BuildForSubmit(new List<AnalyzeRule> { CustomAnalyzeRule() }, existing: new List<RuleSubmission> { PendingRow() });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SubmitAsync(TenantId, Request("ANALYZE-CUSTOM-003"), "alice@contoso.com", "Alice Admin"));
    }

    [Fact]
    public async Task Submit_enforces_the_daily_cap()
    {
        var existing = Enumerable.Range(0, RuleSubmissionService.MaxSubmissionsPerTenantPerDay)
            .Select(i => { var r = PendingRow(); r.SourceRuleId = $"OTHER-{i}"; r.Status = RuleSubmissionStatuses.Declined; return r; })
            .ToList();
        var (service, _) = BuildForSubmit(new List<AnalyzeRule> { CustomAnalyzeRule() }, existing);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SubmitAsync(TenantId, Request("ANALYZE-CUSTOM-003"), "alice@contoso.com", "Alice Admin"));
    }

    [Fact]
    public async Task Submit_organization_attribution_uses_the_company_name_and_fails_without_one()
    {
        var withName = new TenantConfiguration { TenantId = TenantId, DomainName = "contoso.example", CompanyName = "Contoso Ltd." };
        var (service, _) = BuildForSubmit(new List<AnalyzeRule> { CustomAnalyzeRule() }, config: withName);
        var request = Request("ANALYZE-CUSTOM-003");
        request.AttributionMode = RuleAttributionModes.Organization;

        var created = await service.SubmitAsync(TenantId, request, "alice@contoso.com", "Alice Admin");
        Assert.Equal("Contoso Ltd.", created[0].AttributionName);

        var (noConfig, _) = BuildForSubmit(new List<AnalyzeRule> { CustomAnalyzeRule() }, config: null);
        await Assert.ThrowsAsync<ArgumentException>(() => noConfig.SubmitAsync(TenantId, request, "alice@contoso.com", "Alice Admin"));
    }

    [Fact]
    public async Task Submit_stores_the_contact_email_when_given()
    {
        var (service, _) = BuildForSubmit(new List<AnalyzeRule> { CustomAnalyzeRule() });
        var request = Request("ANALYZE-CUSTOM-003");
        request.Email = " alice.reply@contoso.com ";

        var created = await service.SubmitAsync(TenantId, request, "alice@contoso.com", "Alice Admin");
        Assert.Equal("alice.reply@contoso.com", created[0].ContactEmail);
    }

    [Fact]
    public async Task Submit_person_attribution_freezes_the_requested_name()
    {
        var (service, _) = BuildForSubmit(new List<AnalyzeRule> { CustomAnalyzeRule() });
        var request = Request("ANALYZE-CUSTOM-003");
        request.AttributionMode = RuleAttributionModes.Person;
        request.AttributionName = "  Alice A.  ";

        var created = await service.SubmitAsync(TenantId, request, "alice@contoso.com", "Alice Admin");
        Assert.Equal("Alice A.", created[0].AttributionName);
        Assert.Equal(RuleAttributionModes.Person, created[0].AttributionMode);
    }
}
