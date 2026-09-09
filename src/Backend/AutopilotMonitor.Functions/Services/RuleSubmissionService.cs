using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Functions.Rules;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Pagination;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Community rule submissions: freezes a tenant's custom rule at submit time, records the
    /// pre-flight findings and fire telemetry, and carries the review through to the repo file.
    /// Publishing itself is a repository commit plus reseed — this service never writes the
    /// global rule partition; <c>published</c> is derived from the catalog on every read.
    /// </summary>
    public class RuleSubmissionService
    {
        public const int MaxRuleJsonLength = 32_000;
        public const int MaxSubmissionsPerTenantPerDay = 10;
        public const int FireStatsDays = 30;

        /// <summary>The credit text is published in the repo, so it is a name and nothing else.</summary>
        private static readonly Regex AttributionNameShape = new(@"^[\p{L}\p{N} .,'&()\-]{1,64}$", RegexOptions.CultureInvariant);

        /// <summary>Rule fields that belong to the server or the tenant, never to a frozen or published rule.</summary>
        private static readonly string[] StrippedFields =
        {
            "isBuiltIn", "provenance", "createdAt", "updatedAt",
            "markSessionAsFailed", "notifyDefault", "notify", "notifyChannelIds",
        };

        private static readonly JsonSerializerOptions RepoFileOptions = new()
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        // The dominant convention per kind in rules/ (ANALYZE-APP vs GATHER-APPS …); an unmapped
        // category falls back to its upper-cased name, which the reviewer can overrule.
        private static readonly Dictionary<string, string> AnalyzeCategoryCodes = new(StringComparer.OrdinalIgnoreCase)
        {
            ["apps"] = "APP", ["device"] = "DEV", ["enrollment"] = "ENRL", ["esp"] = "ESP",
            ["identity"] = "ID", ["network"] = "NET", ["security"] = "SEC",
        };
        private static readonly Dictionary<string, string> GatherCategoryCodes = new(StringComparer.OrdinalIgnoreCase)
        {
            ["apps"] = "APPS", ["device"] = "DEVICE", ["enrollment"] = "ENROLL", ["esp"] = "ESP",
            ["identity"] = "ID", ["network"] = "NET",
        };

        private readonly IRuleSubmissionRepository _submissions;
        private readonly IRuleRepository _ruleRepo;
        private readonly IMetricsRepository _metricsRepo;
        private readonly TenantConfigurationService _configService;
        private readonly ILogger<RuleSubmissionService> _logger;

        public RuleSubmissionService(
            IRuleSubmissionRepository submissions,
            IRuleRepository ruleRepo,
            IMetricsRepository metricsRepo,
            TenantConfigurationService configService,
            ILogger<RuleSubmissionService> logger)
        {
            _submissions = submissions;
            _ruleRepo = ruleRepo;
            _metricsRepo = metricsRepo;
            _configService = configService;
            _logger = logger;
        }

        // ── Submit ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Freezes and stores every referenced rule. All-or-nothing: a pre-flight error on any
        /// item rejects the whole batch (<see cref="ArgumentException"/>), so a tenant never ends
        /// up with half a submission. <see cref="InvalidOperationException"/> = conflict (open
        /// duplicate, daily cap).
        /// </summary>
        public async Task<List<RuleSubmission>> SubmitAsync(
            string tenantId, SubmitRuleSubmissionsRequest request, string submittedBy, string submittedByName)
        {
            if (request.Items == null || request.Items.Count == 0)
                throw new ArgumentException("At least one rule is required.");
            if (request.Items.Count > SubmitRuleSubmissionsRequest.MaxItems)
                throw new ArgumentException($"At most {SubmitRuleSubmissionsRequest.MaxItems} rules per submission.");
            if (request.Comment != null && request.Comment.Length > SubmitRuleSubmissionsRequest.MaxCommentLength)
                throw new ArgumentException($"Comment exceeds {SubmitRuleSubmissionsRequest.MaxCommentLength} characters.");

            var attributionName = await ResolveAttributionNameAsync(tenantId, request.AttributionMode, request.AttributionName);

            var distinct = request.Items
                .Select(i => (Kind: i.Kind?.Trim().ToLowerInvariant() ?? string.Empty, RuleId: i.RuleId?.Trim() ?? string.Empty))
                .Distinct()
                .ToList();
            foreach (var (kind, ruleId) in distinct)
            {
                if (!RuleSubmissionKinds.IsKnown(kind))
                    throw new ArgumentException($"Unknown rule kind '{kind}'.");
                if (string.IsNullOrEmpty(ruleId))
                    throw new ArgumentException("ruleId is required for every item.");
            }

            var existing = await _submissions.GetForTenantAsync(tenantId);
            var since = DateTime.UtcNow.AddDays(-1);
            if (existing.Count(s => s.SubmittedAt >= since) + distinct.Count > MaxSubmissionsPerTenantPerDay)
                throw new InvalidOperationException($"At most {MaxSubmissionsPerTenantPerDay} rule submissions per tenant and day.");

            var analyzeRules = distinct.Any(d => d.Kind == RuleSubmissionKinds.Analyze)
                ? await _ruleRepo.GetAnalyzeRulesAsync(tenantId) : new List<AnalyzeRule>();
            var gatherRules = distinct.Any(d => d.Kind == RuleSubmissionKinds.Gather)
                ? await _ruleRepo.GetGatherRulesAsync(tenantId) : new List<GatherRule>();

            var now = DateTime.UtcNow;
            var batchId = NewId();
            var prepared = new List<RuleSubmission>(distinct.Count);
            var errors = new List<string>();

            foreach (var (kind, ruleId) in distinct)
            {
                if (existing.Any(s => s.Status == RuleSubmissionStatuses.Pending
                                      && s.RuleKind == kind
                                      && string.Equals(s.SourceRuleId, ruleId, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException($"Rule '{ruleId}' already has a pending submission.");

                var submission = new RuleSubmission
                {
                    SubmissionId = NewId(),
                    BatchId = batchId,
                    TenantId = tenantId,
                    RuleKind = kind,
                    SourceRuleId = ruleId,
                    Comment = string.IsNullOrWhiteSpace(request.Comment) ? null : request.Comment!.Trim(),
                    SubmittedBy = submittedBy,
                    SubmittedByName = submittedByName,
                    AttributionMode = request.AttributionMode,
                    AttributionName = attributionName,
                    SubmittedAt = now,
                    Status = RuleSubmissionStatuses.Pending,
                };

                var findings = new List<RuleSubmissionFinding>();
                if (kind == RuleSubmissionKinds.Analyze)
                {
                    var rule = analyzeRules.FirstOrDefault(r => string.Equals(r.RuleId, ruleId, StringComparison.OrdinalIgnoreCase)
                                                              && !r.IsBuiltIn && !r.IsCommunity);
                    if (rule == null) { errors.Add($"{ruleId}: not a custom analyze rule of this tenant."); continue; }

                    submission.Title = rule.Title ?? string.Empty;
                    submission.Category = rule.Category ?? string.Empty;
                    submission.DerivedFromTemplateRuleId = rule.DerivedFromTemplateRuleId;
                    submission.RuleJson = FreezeRule(rule);
                    foreach (var e in DryRunAnalyzeRuleFunction.ValidateDraftRule(rule))
                        findings.Add(new RuleSubmissionFinding { Level = "error", Message = e });
                    if (!string.IsNullOrEmpty(rule.DerivedFromTemplateRuleId))
                        findings.Add(new RuleSubmissionFinding { Level = "info", Message = $"Derived from template {rule.DerivedFromTemplateRuleId} — usually a filled-in copy rather than a new detection." });
                }
                else
                {
                    var rule = gatherRules.FirstOrDefault(r => string.Equals(r.RuleId, ruleId, StringComparison.OrdinalIgnoreCase)
                                                             && !r.IsBuiltIn && !r.IsCommunity);
                    if (rule == null) { errors.Add($"{ruleId}: not a custom gather rule of this tenant."); continue; }

                    submission.Title = rule.Title ?? string.Empty;
                    submission.Category = rule.Category ?? string.Empty;
                    submission.RuleJson = FreezeRule(rule);
                    if (string.IsNullOrWhiteSpace(rule.CollectorType)) findings.Add(new RuleSubmissionFinding { Level = "error", Message = "collectorType is required" });
                    if (string.IsNullOrWhiteSpace(rule.Target)) findings.Add(new RuleSubmissionFinding { Level = "error", Message = "target is required" });
                    if (string.IsNullOrWhiteSpace(rule.Trigger)) findings.Add(new RuleSubmissionFinding { Level = "error", Message = "trigger is required" });
                    if (string.IsNullOrWhiteSpace(rule.OutputEventType)) findings.Add(new RuleSubmissionFinding { Level = "error", Message = "outputEventType is required" });
                    var scopeError = GatherRulesFunction.ValidateScopeAndEmitMode(rule);
                    if (scopeError != null) findings.Add(new RuleSubmissionFinding { Level = "error", Message = scopeError });
                    findings.Add(new RuleSubmissionFinding { Level = "info", Message = "Collector guardrails are enforced on the device; the full allowlist check runs in the MCP validate_rule tool." });
                }

                if (submission.RuleJson.Length > MaxRuleJsonLength)
                    findings.Add(new RuleSubmissionFinding { Level = "error", Message = $"rule document exceeds {MaxRuleJsonLength} characters" });

                var itemErrors = findings.Where(f => f.Level == "error").Select(f => $"{ruleId}: {f.Message}").ToList();
                if (itemErrors.Count > 0) { errors.AddRange(itemErrors); continue; }

                submission.ValidationFindings = findings;
                submission.SourceFireStats = await GetFireStatsAsync(tenantId, kind, ruleId);
                prepared.Add(submission);
            }

            if (errors.Count > 0)
                throw new ArgumentException("Submission rejected: " + string.Join(" | ", errors));

            foreach (var submission in prepared)
            {
                if (!await _submissions.AddAsync(submission))
                    throw new InvalidOperationException("Failed to store the submission.");
            }

            return prepared;
        }

        private async Task<string> ResolveAttributionNameAsync(string tenantId, string? mode, string? requestedName)
        {
            switch (mode)
            {
                case RuleAttributionModes.Anonymous:
                    return RuleAttributionModes.AnonymousAuthor;
                case RuleAttributionModes.Organization:
                {
                    var config = await _configService.GetConfigurationIfExistsAsync(tenantId);
                    var name = config?.CompanyName;
                    if (string.IsNullOrWhiteSpace(name)) name = config?.DomainName;
                    if (string.IsNullOrWhiteSpace(name))
                        throw new ArgumentException("No organization name is known for this tenant — set the company name in the tenant settings or choose another attribution.");
                    return ValidateAttributionName(name!.Trim());
                }
                case RuleAttributionModes.Person:
                    if (string.IsNullOrWhiteSpace(requestedName))
                        throw new ArgumentException("attributionName is required for the person attribution.");
                    return ValidateAttributionName(requestedName!.Trim());
                default:
                    throw new ArgumentException($"attributionMode must be one of: {string.Join(", ", RuleAttributionModes.All)}.");
            }
        }

        internal static string ValidateAttributionName(string name)
        {
            if (!AttributionNameShape.IsMatch(name))
                throw new ArgumentException("attributionName may only contain letters, digits, spaces and . , ' & ( ) - (1–64 characters).");
            return name;
        }

        /// <summary>Wire JSON of the rule minus server and tenant fields — what the reviewer sees and what ships.</summary>
        internal static string FreezeRule<T>(T rule)
        {
            var node = JsonSerializer.SerializeToNode(rule, ApiJsonOptions.Create()) as JsonObject
                ?? throw new InvalidOperationException("Rule did not serialize to an object.");
            foreach (var field in StrippedFields) node.Remove(field);
            return node.ToJsonString();
        }

        private async Task<RuleSubmissionFireStats?> GetFireStatsAsync(string tenantId, string kind, string ruleId)
        {
            try
            {
                var end = DateTime.UtcNow;
                var start = end.AddDays(-FireStatsDays);
                var entries = await _metricsRepo.GetRuleStatsAsync(
                    tenantId, start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), kind);
                var mine = entries.Where(e => string.Equals(e.RuleId, ruleId, StringComparison.OrdinalIgnoreCase)).ToList();
                return new RuleSubmissionFireStats
                {
                    Days = FireStatsDays,
                    FireCount = mine.Sum(e => e.FireCount),
                    SessionsEvaluated = mine.Sum(e => e.SessionsEvaluated),
                    EvaluationCount = mine.Sum(e => e.EvaluationCount),
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Fire stats unavailable for {RuleId} (tenant {TenantId})", ruleId, tenantId);
                return null;
            }
        }

        // ── Read ────────────────────────────────────────────────────────────────

        public Task<RuleSubmission?> GetAsync(string submissionId) => _submissions.GetAsync(submissionId);

        public Task<List<RuleSubmission>> GetForTenantAsync(string tenantId) => _submissions.GetForTenantAsync(tenantId);

        public Task<RawPage<RuleSubmission>> GetPageAsync(string? tenantId, string? status, int pageSize, string? continuation)
            => _submissions.GetPageAsync(tenantId, status, pageSize, continuation);

        /// <summary>Projects rows to the wire shape with the effective status (<c>published</c> derived from the catalog).</summary>
        public async Task<List<RuleSubmissionItem>> ToItemsAsync(IEnumerable<RuleSubmission> rows)
        {
            var list = rows.ToList();
            HashSet<string>? globalAnalyze = null, globalGather = null;
            if (list.Any(s => s.Status == RuleSubmissionStatuses.Approved && !string.IsNullOrEmpty(s.PublishedRuleId)))
            {
                globalAnalyze = (await _ruleRepo.GetAnalyzeRulesAsync("global")).Select(r => r.RuleId).ToHashSet(StringComparer.OrdinalIgnoreCase);
                globalGather = (await _ruleRepo.GetGatherRulesAsync("global")).Select(r => r.RuleId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            }

            return list.Select(s => ToItem(s, IsPublished(s, globalAnalyze, globalGather))).ToList();
        }

        private static bool IsPublished(RuleSubmission s, HashSet<string>? globalAnalyze, HashSet<string>? globalGather)
        {
            if (s.Status != RuleSubmissionStatuses.Approved || string.IsNullOrEmpty(s.PublishedRuleId)) return false;
            var set = s.RuleKind == RuleSubmissionKinds.Analyze ? globalAnalyze : globalGather;
            return set != null && set.Contains(s.PublishedRuleId!);
        }

        internal static RuleSubmissionItem ToItem(RuleSubmission s, bool published) => new()
        {
            SubmissionId = s.SubmissionId,
            BatchId = s.BatchId,
            TenantId = s.TenantId,
            RuleKind = s.RuleKind,
            SourceRuleId = s.SourceRuleId,
            Title = s.Title,
            Category = s.Category,
            Comment = s.Comment,
            SubmittedBy = s.SubmittedBy,
            SubmittedByName = s.SubmittedByName,
            AttributionMode = s.AttributionMode,
            AttributionName = s.AttributionName,
            SubmittedAt = s.SubmittedAt,
            Status = published ? RuleSubmissionStatuses.Published : s.Status,
            ValidationFindings = s.ValidationFindings,
            SourceFireStats = s.SourceFireStats,
            ReviewedBy = s.ReviewedBy,
            ReviewedAt = s.ReviewedAt,
            ReviewComment = s.ReviewComment,
            WillBeAdapted = s.WillBeAdapted,
            PublishedRuleId = s.PublishedRuleId,
            DerivedFromTemplateRuleId = s.DerivedFromTemplateRuleId,
        };

        /// <summary>Everything the reviewer (human or AI) needs for one submission.</summary>
        public async Task<RuleSubmissionDetailResponse?> GetDetailAsync(string submissionId)
        {
            var s = await _submissions.GetAsync(submissionId);
            if (s == null) return null;

            var item = (await ToItemsAsync(new[] { s }))[0];
            var response = new RuleSubmissionDetailResponse { Success = true, Submission = item };

            if (s.RuleKind == RuleSubmissionKinds.Analyze)
            {
                var rule = JsonSerializer.Deserialize<AnalyzeRule>(s.RuleJson, ApiJsonOptions.Read);
                if (rule != null) { rule.IsBuiltIn = false; rule.IsCommunity = false; }
                response.AnalyzeRule = rule;
            }
            else
            {
                var rule = JsonSerializer.Deserialize<GatherRule>(s.RuleJson, ApiJsonOptions.Read);
                if (rule != null) { rule.IsBuiltIn = false; rule.IsCommunity = false; }
                response.GatherRule = rule;
            }

            response.LiveFireStats = await GetFireStatsAsync(s.TenantId, s.RuleKind, s.SourceRuleId);
            response.SuggestedPublishedRuleId = s.PublishedRuleId ?? await SuggestPublishedRuleIdAsync(s.RuleKind, s.Category);
            var fileId = s.PublishedRuleId ?? response.SuggestedPublishedRuleId;
            if (!string.IsNullOrEmpty(fileId))
                response.RepoFile = BuildRepoFile(s, fileId!);
            return response;
        }

        // ── Review ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Applies a reviewer decision. Transitions: pending → approved | declined; approved → declined
        /// while not yet published. <see cref="KeyNotFoundException"/> = unknown id,
        /// <see cref="ArgumentException"/> = bad input, <see cref="InvalidOperationException"/> = conflict.
        /// </summary>
        public async Task<RuleSubmission> ReviewAsync(string submissionId, ReviewRuleSubmissionRequest request, string reviewedBy)
        {
            var s = await _submissions.GetAsync(submissionId) ?? throw new KeyNotFoundException("Submission not found.");
            var comment = string.IsNullOrWhiteSpace(request.ReviewComment) ? null : request.ReviewComment!.Trim();
            if (comment != null && comment.Length > ReviewRuleSubmissionRequest.MaxReviewCommentLength)
                throw new ArgumentException($"reviewComment exceeds {ReviewRuleSubmissionRequest.MaxReviewCommentLength} characters.");

            var published = (await ToItemsAsync(new[] { s }))[0].Status == RuleSubmissionStatuses.Published;
            if (published)
                throw new InvalidOperationException("The rule is already published; the submission is final.");
            if (s.Status == RuleSubmissionStatuses.Withdrawn)
                throw new InvalidOperationException("The submitter withdrew this submission.");

            switch (request.Decision)
            {
                case RuleSubmissionDecisions.Approve:
                {
                    if (s.Status != RuleSubmissionStatuses.Pending)
                        throw new InvalidOperationException($"Only a pending submission can be approved (status: {s.Status}).");
                    var publishedRuleId = request.PublishedRuleId?.Trim();
                    if (string.IsNullOrEmpty(publishedRuleId))
                        throw new ArgumentException("publishedRuleId is required to approve.");
                    await ValidatePublishedRuleIdAsync(s, publishedRuleId!);
                    s.Status = RuleSubmissionStatuses.Approved;
                    s.PublishedRuleId = publishedRuleId;
                    s.WillBeAdapted = request.WillBeAdapted ?? false;
                    break;
                }
                case RuleSubmissionDecisions.Decline:
                {
                    if (s.Status != RuleSubmissionStatuses.Pending && s.Status != RuleSubmissionStatuses.Approved)
                        throw new InvalidOperationException($"Cannot decline a submission in status {s.Status}.");
                    if (comment == null)
                        throw new ArgumentException("reviewComment is required to decline — the submitter sees it.");
                    s.Status = RuleSubmissionStatuses.Declined;
                    s.PublishedRuleId = null;
                    s.WillBeAdapted = false;
                    break;
                }
                default:
                    throw new ArgumentException($"decision must be one of: {string.Join(", ", RuleSubmissionDecisions.All)}.");
            }

            s.ReviewComment = comment;
            s.ReviewedBy = reviewedBy;
            s.ReviewedAt = DateTime.UtcNow;

            if (!await _submissions.UpdateAsync(s))
                throw new InvalidOperationException("The submission changed underneath this review; reload and retry.");
            return s;
        }

        private async Task ValidatePublishedRuleIdAsync(RuleSubmission s, string publishedRuleId)
        {
            var kindPrefix = s.RuleKind == RuleSubmissionKinds.Analyze ? "ANALYZE-" : "GATHER-";
            if (!RuleIdPolicy.IsReservedBuiltInId(publishedRuleId)
                || !publishedRuleId.StartsWith(kindPrefix, StringComparison.OrdinalIgnoreCase)
                || !Regex.IsMatch(publishedRuleId, @"^[A-Z]+-[A-Z]+-\d{3}$"))
                throw new ArgumentException($"publishedRuleId must be {kindPrefix}<CATEGORY>-<NNN> in the reserved built-in namespace (upper case, three digits).");

            var taken = await GetTakenIdsAsync(s.RuleKind);
            if (taken.Contains(publishedRuleId))
                throw new InvalidOperationException($"Rule id '{publishedRuleId}' is already taken (catalog, global partition or another approved submission).");
        }

        /// <summary>Tenant-side withdrawal; only while pending. False when there is nothing to withdraw.</summary>
        public async Task<bool> WithdrawAsync(string tenantId, string submissionId, bool crossTenantAllowed)
        {
            var s = await _submissions.GetAsync(submissionId);
            if (s == null) return false;
            if (!crossTenantAllowed && !string.Equals(s.TenantId, tenantId, StringComparison.OrdinalIgnoreCase)) return false;
            if (s.Status != RuleSubmissionStatuses.Pending)
                throw new InvalidOperationException($"Only a pending submission can be withdrawn (status: {s.Status}).");
            s.Status = RuleSubmissionStatuses.Withdrawn;
            return await _submissions.UpdateAsync(s);
        }

        // ── Id suggestion + repo file ───────────────────────────────────────────

        private async Task<HashSet<string>> GetTakenIdsAsync(string kind)
        {
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (kind == RuleSubmissionKinds.Analyze)
            {
                foreach (var r in BuiltInAnalyzeRules.GetAll()) taken.Add(r.RuleId);
                foreach (var r in await _ruleRepo.GetAnalyzeRulesAsync("global")) taken.Add(r.RuleId);
            }
            else
            {
                foreach (var r in BuiltInGatherRules.GetAll()) taken.Add(r.RuleId);
                foreach (var r in await _ruleRepo.GetGatherRulesAsync("global")) taken.Add(r.RuleId);
            }
            foreach (var id in await _submissions.GetReservedPublishedRuleIdsAsync()) taken.Add(id);
            return taken;
        }

        public async Task<string> SuggestPublishedRuleIdAsync(string kind, string category)
            => SuggestPublishedRuleId(kind, category, await GetTakenIdsAsync(kind));

        /// <summary>
        /// Max + 1 within <c>{KIND}-{CODE}-</c> over every taken id. Gaps are retired ids that may
        /// return (RuleIdPolicy) and are never reused.
        /// </summary>
        internal static string SuggestPublishedRuleId(string kind, string category, IEnumerable<string> takenIds)
        {
            var codes = kind == RuleSubmissionKinds.Analyze ? AnalyzeCategoryCodes : GatherCategoryCodes;
            var code = codes.TryGetValue(category ?? string.Empty, out var mapped)
                ? mapped
                : Regex.Replace((category ?? "MISC").ToUpperInvariant(), "[^A-Z]", "");
            if (code.Length == 0) code = "MISC";
            var prefix = (kind == RuleSubmissionKinds.Analyze ? "ANALYZE-" : "GATHER-") + code + "-";

            var max = 0;
            foreach (var id in takenIds)
            {
                if (!id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (int.TryParse(id.Substring(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > max)
                    max = n;
            }
            return prefix + (max + 1).ToString("D3", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// The file that ships the rule: schema pointer first, the assigned id, the chosen credit,
        /// <c>isCommunity</c>, and <c>enabled</c> after the built-in convention (analyze rules ship
        /// on, gather rules opt-in because they cost device work). Lineage to the tenant's template
        /// copy is dropped.
        /// </summary>
        internal static RuleSubmissionRepoFile BuildRepoFile(RuleSubmission s, string publishedRuleId)
        {
            var frozen = JsonNode.Parse(s.RuleJson) as JsonObject
                ?? throw new InvalidOperationException("Frozen rule is not an object.");
            var isAnalyze = s.RuleKind == RuleSubmissionKinds.Analyze;

            var ordered = new JsonObject
            {
                ["$schema"] = isAnalyze ? "../schema/analyze-rule.schema.json" : "../schema/gather-rule.schema.json",
                ["ruleId"] = publishedRuleId,
            };
            foreach (var head in isAnalyze
                         ? new[] { "title", "description", "severity", "category", "version" }
                         : new[] { "title", "description", "category", "version" })
            {
                if (frozen.TryGetPropertyValue(head, out var value)) ordered[head] = value?.DeepClone();
            }
            ordered["author"] = s.AttributionName;
            ordered["enabled"] = isAnalyze;
            ordered["isCommunity"] = true;

            foreach (var kv in frozen)
            {
                if (ordered.ContainsKey(kv.Key)) continue;
                if (kv.Key == "derivedFromTemplateRuleId") continue;
                if (Array.IndexOf(StrippedFields, kv.Key) >= 0) continue;
                ordered[kv.Key] = kv.Value?.DeepClone();
            }

            var folder = isAnalyze ? "analyze" : "gather";
            return new RuleSubmissionRepoFile
            {
                Path = $"rules/{folder}/{publishedRuleId}.json",
                Content = ordered.ToJsonString(RepoFileOptions) + "\n",
            };
        }

        private static string NewId() => Guid.NewGuid().ToString("N").Substring(0, 12);
    }
}
