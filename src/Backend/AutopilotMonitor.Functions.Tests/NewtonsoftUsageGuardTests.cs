using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Contains Newtonsoft.Json (D-420): new code reads and writes JSON with System.Text.Json. Every shipped
/// source file under <c>src/</c> that uses Newtonsoft today is on a frozen baseline that may only shrink,
/// and only the projects in <see cref="ProjectsWithPackage"/> may reference the package. Test projects are
/// out of scope: they exercise Newtonsoft-bound code and never ship.
/// A file uses Newtonsoft when a code line names the <c>Newtonsoft.Json</c> namespace (using, alias,
/// fully qualified name, or the DLL name); comment lines do not count.
/// </summary>
public class NewtonsoftUsageGuardTests
{
    private const string AgentWireAndState = "agent (net48): wire format and on-device state, which move only with an agent release";
    private const string DecisionCore = "DecisionCore serialization shared by the agent and its tests";
    private const string SummaryDialog = "summary dialog (net48): reads the agent's final status and checks the DLL ships beside it";
    private const string Ingest = "agent ingest wire (D-200)";
    private const string ConfigRules = "Newtonsoft-bound configuration and rule documents (PopulateObject)";
    private const string QueueEnvelope = "queue envelope contract shared by producers and consumers";
    private const string StoredJson = "stored JSON (table columns, blobs, report files) that existing rows must keep reading";
    private const string OutgoingPayload = "outgoing notification payloads";
    private const string RuleTemplates = "rule template interpolation over Newtonsoft tokens";

    /// <summary>Path relative to <c>src/</c> → why it may still use Newtonsoft. Only shrinks.</summary>
    private static readonly Dictionary<string, string> Baseline = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Agent/AutopilotMonitor.Agent.V2.Core/Configuration/BuiltInConfigCatalog.cs"] = AgentWireAndState,
        ["Agent/AutopilotMonitor.Agent.V2.Core/Configuration/RemoteConfigService.cs"] = AgentWireAndState,
        ["Agent/AutopilotMonitor.Agent.V2.Core/Diagnostics/CrashDumpCapture.cs"] = AgentWireAndState,
        ["Agent/AutopilotMonitor.Agent.V2.Core/Diagnostics/CrashRecord.cs"] = AgentWireAndState,
        ["Agent/AutopilotMonitor.Agent.V2.Core/Diagnostics/PendingCrashReporter.cs"] = AgentWireAndState,
        ["Agent/AutopilotMonitor.Agent.V2.Core/Monitoring/Enrollment/Ime/ImeLogTracker.cs"] = AgentWireAndState,
        ["Agent/AutopilotMonitor.Agent.V2.Core/Monitoring/Enrollment/Ime/ImeLogTracker.Handlers.cs"] = AgentWireAndState,
        ["Agent/AutopilotMonitor.Agent.V2.Core/Monitoring/Enrollment/Ime/ImeTrackerStatePersistence.cs"] = AgentWireAndState,
        ["Agent/AutopilotMonitor.Agent.V2.Core/Monitoring/Enrollment/SystemSignals/EventRecordWatermark.cs"] = AgentWireAndState,
        ["Agent/AutopilotMonitor.Agent.V2.Core/Monitoring/Enrollment/SystemSignals/MdmRebootPolicyTracker.cs"] = AgentWireAndState,
        ["Agent/AutopilotMonitor.Agent.V2.Core/Monitoring/Enrollment/SystemSignals/ModernDeploymentTracker.cs"] = AgentWireAndState,
        ["Agent/AutopilotMonitor.Agent.V2.Core/Monitoring/Enrollment/SystemSignals/SystemTimelineTracker.cs"] = AgentWireAndState,
        ["Agent/AutopilotMonitor.Agent.V2.Core/Monitoring/Runtime/SelfUpdater.cs"] = AgentWireAndState,
        ["Agent/AutopilotMonitor.Agent.V2.Core/Monitoring/Runtime/VersionCheckEventBuilder.cs"] = AgentWireAndState,
        ["Agent/AutopilotMonitor.Agent.V2.Core/Monitoring/Telemetry/Gather/Collectors/JsonCollector.cs"] = AgentWireAndState,
        ["Agent/AutopilotMonitor.Agent.V2.Core/Monitoring/Telemetry/Gather/GatherRuleGuards.cs"] = AgentWireAndState,
        ["Agent/AutopilotMonitor.Agent.V2.Core/Monitoring/Telemetry/Gather/GatherRuleStatePersistence.cs"] = AgentWireAndState,
        ["Agent/AutopilotMonitor.Agent.V2.Core/Monitoring/Telemetry/Office/OfficeInstallStatePersistence.cs"] = AgentWireAndState,
        ["Agent/AutopilotMonitor.Agent.V2.Core/Monitoring/Telemetry/Periodic/BandwidthStatePersistence.cs"] = AgentWireAndState,
        ["Agent/AutopilotMonitor.Agent.V2.Core/Monitoring/Telemetry/Periodic/DeliveryOptimizationCollector.cs"] = AgentWireAndState,
        ["Agent/AutopilotMonitor.Agent.V2.Core/Monitoring/Telemetry/Provisioning/ProvisioningPackageCollector.cs"] = AgentWireAndState,
        ["Agent/AutopilotMonitor.Agent.V2.Core/Monitoring/Transport/BackendApiClient.cs"] = AgentWireAndState,
        ["Agent/AutopilotMonitor.Agent.V2.Core/Monitoring/Transport/DistressReporter.cs"] = AgentWireAndState,
        ["Agent/AutopilotMonitor.Agent.V2.Core/Monitoring/Transport/GeoLocationService.cs"] = AgentWireAndState,
        ["Agent/AutopilotMonitor.Agent.V2.Core/Orchestration/Hosts/OsBuildChangeHost.cs"] = AgentWireAndState,
        ["Agent/AutopilotMonitor.Agent.V2.Core/Persistence/StartupEventGate.cs"] = AgentWireAndState,
        ["Agent/AutopilotMonitor.Agent.V2.Core/Security/SessionIdPersistence.cs"] = AgentWireAndState,
        ["Agent/AutopilotMonitor.Agent.V2.Core/Telemetry/Events/EventSequencePersistence.cs"] = AgentWireAndState,
        ["Agent/AutopilotMonitor.Agent.V2.Core/Telemetry/Events/TelemetryEventEmitter.cs"] = AgentWireAndState,
        ["Agent/AutopilotMonitor.Agent.V2.Core/Telemetry/Signals/TelemetrySignalEmitter.cs"] = AgentWireAndState,
        ["Agent/AutopilotMonitor.Agent.V2.Core/Telemetry/Transitions/TelemetryTransitionEmitter.cs"] = AgentWireAndState,
        ["Agent/AutopilotMonitor.Agent.V2.Core/Termination/FinalStatus.cs"] = AgentWireAndState,
        ["Agent/AutopilotMonitor.Agent.V2.Core/Termination/SummaryDialogLauncher.cs"] = AgentWireAndState,
        ["Agent/AutopilotMonitor.Agent.V2.Core/Transport/Telemetry/BackendTelemetryUploader.cs"] = AgentWireAndState,
        ["Agent/AutopilotMonitor.Agent.V2.Core/Transport/Telemetry/TelemetryItemSerializer.cs"] = AgentWireAndState,
        ["Agent/AutopilotMonitor.Agent.V2.Core/Transport/Telemetry/UploadCursorPersistence.cs"] = AgentWireAndState,
        ["Agent/AutopilotMonitor.Agent.V2/Program.GatherRulesMode.cs"] = AgentWireAndState,
        ["Agent/AutopilotMonitor.Agent.V2/Program.Guards.cs"] = AgentWireAndState,
        ["Agent/AutopilotMonitor.Agent.V2/Program.ImeMatchingMode.cs"] = AgentWireAndState,
        ["Agent/AutopilotMonitor.Agent.V2/Program.InstallMode.cs"] = AgentWireAndState,

        ["Shared/AutopilotMonitor.DecisionCore/Serialization/DecisionCoreJsonSettings.cs"] = DecisionCore,
        ["Shared/AutopilotMonitor.DecisionCore/Serialization/SignalSerializer.cs"] = DecisionCore,
        ["Shared/AutopilotMonitor.DecisionCore/Serialization/StateSerializer.cs"] = DecisionCore,
        ["Shared/AutopilotMonitor.DecisionCore/Serialization/TransitionSerializer.cs"] = DecisionCore,
        ["Shared/AutopilotMonitor.DecisionCore/Serialization/UnknownFallbackEnumConverter.cs"] = DecisionCore,

        ["Agent/AutopilotMonitor.SummaryDialog/App.xaml.cs"] = SummaryDialog,
        ["Agent/AutopilotMonitor.SummaryDialog/MainWindow.xaml.cs"] = SummaryDialog,
        ["Agent/AutopilotMonitor.SummaryDialog/Models/FinalStatus.cs"] = SummaryDialog,

        ["Backend/AutopilotMonitor.Functions/Functions/Bootstrap/BootstrapRegisterSessionFunction.cs"] = Ingest,
        ["Backend/AutopilotMonitor.Functions/Functions/Ingest/EventDataNormalizer.cs"] = Ingest,
        ["Backend/AutopilotMonitor.Functions/Functions/Ingest/IngestTelemetryFunction.cs"] = Ingest,
        ["Backend/AutopilotMonitor.Functions/Functions/Ingest/TelemetryPayloadParser.cs"] = Ingest,
        ["Backend/AutopilotMonitor.Functions/Functions/Sessions/RegisterSessionFunction.cs"] = Ingest,
        ["Backend/AutopilotMonitor.Functions/Services/Ime/ImePatternHealthService.cs"] = Ingest,
        ["Backend/AutopilotMonitor.Functions/Services/Vulnerability/VulnerabilityInventoryLoader.cs"] = Ingest,

        ["Backend/AutopilotMonitor.Functions/Functions/Config/PatchAdminConfigurationFunction.cs"] = ConfigRules,
        ["Backend/AutopilotMonitor.Functions/Functions/Config/PatchTenantConfigurationFieldsFunction.cs"] = ConfigRules,
        ["Backend/AutopilotMonitor.Functions/Helpers/RequestBody.cs"] = ConfigRules,
        ["Backend/AutopilotMonitor.Functions/Services/BuiltInAnalyzeRules.cs"] = ConfigRules,
        ["Backend/AutopilotMonitor.Functions/Services/BuiltInGatherRules.cs"] = ConfigRules,
        ["Backend/AutopilotMonitor.Functions/Services/BuiltInImeLogPatterns.cs"] = ConfigRules,
        ["Backend/AutopilotMonitor.Functions/Services/GitHubRuleRepository.cs"] = ConfigRules,
        ["Backend/AutopilotMonitor.Functions/Services/RulesWrapper.cs"] = ConfigRules,
        ["Backend/AutopilotMonitor.Functions/Services/TenantConfigPatchService.cs"] = ConfigRules,

        ["Backend/AutopilotMonitor.Functions/Services/Activation/AzureQueueTenantAutoApproveEnqueuer.cs"] = QueueEnvelope,
        ["Backend/AutopilotMonitor.Functions/Services/Analyze/AzureQueueAnalyzeOnEnrollmentEndProducer.cs"] = QueueEnvelope,
        ["Backend/AutopilotMonitor.Functions/Services/Backup/Queue/AzureQueueCriticalTableBackupProducer.cs"] = QueueEnvelope,
        ["Backend/AutopilotMonitor.Functions/Services/Backup/Queue/CriticalTableBackupQueueWorker.cs"] = QueueEnvelope,
        ["Backend/AutopilotMonitor.Functions/Services/Deletion/AzureQueueSessionDeletionMaintenanceTriggerProducer.cs"] = QueueEnvelope,
        ["Backend/AutopilotMonitor.Functions/Services/Deletion/SessionDeletionProducer.cs"] = QueueEnvelope,
        ["Backend/AutopilotMonitor.Functions/Services/Deletion/SessionDeletionWorker.cs"] = QueueEnvelope,
        ["Backend/AutopilotMonitor.Functions/Services/Ime/AzureQueueImeMsiArchiveProducer.cs"] = QueueEnvelope,
        ["Backend/AutopilotMonitor.Functions/Services/Maintenance/AzureQueueMaintenanceTriggerProducer.cs"] = QueueEnvelope,
        ["Backend/AutopilotMonitor.Functions/Services/Notifications/AzureQueueNotificationDispatchProducer.cs"] = QueueEnvelope,
        ["Backend/AutopilotMonitor.Functions/Services/Offboarding/AzureQueueTenantOffboardingEnqueuer.cs"] = QueueEnvelope,
        ["Backend/AutopilotMonitor.Functions/Services/Offboarding/TenantOffboardingWorker.cs"] = QueueEnvelope,
        ["Backend/AutopilotMonitor.Functions/Services/Queueing/QueueEnvelopeReader.cs"] = QueueEnvelope,
        ["Backend/AutopilotMonitor.Functions/Services/Queueing/QueuePollingWorker.cs"] = QueueEnvelope,
        ["Backend/AutopilotMonitor.Functions/Services/Vulnerability/AzureQueueVulnerabilityCorrelateProducer.cs"] = QueueEnvelope,

        ["Backend/AutopilotMonitor.Functions/Services/AnalyzeRuleService.cs"] = StoredJson,
        ["Backend/AutopilotMonitor.Functions/Services/FailureSnapshotBuilder.cs"] = StoredJson,
        ["Backend/AutopilotMonitor.Functions/Services/MaintenanceService.Aggregation.cs"] = StoredJson,
        ["Backend/AutopilotMonitor.Functions/Services/SessionReportService.cs"] = StoredJson,
        ["Backend/AutopilotMonitor.Functions/Services/TableStorageService.cs"] = StoredJson,
        ["Backend/AutopilotMonitor.Functions/Services/TableStorageService.Maintenance.cs"] = StoredJson,
        ["Backend/AutopilotMonitor.Functions/Services/TableStorageService.Rules.cs"] = StoredJson,
        ["Backend/AutopilotMonitor.Functions/Services/TableStorageService.ServerActions.cs"] = StoredJson,
        ["Backend/AutopilotMonitor.Functions/Services/TableStorageService.Sessions.cs"] = StoredJson,
        ["Backend/AutopilotMonitor.Functions/Services/Vulnerability/VulnerabilityCorrelateHandler.cs"] = StoredJson,
        ["Backend/AutopilotMonitor.Functions/Services/Vulnerability/VulnerabilityCorrelationService.cs"] = StoredJson,

        ["Backend/AutopilotMonitor.Functions/Services/Notifications/DiscordRenderer.cs"] = OutgoingPayload,
        ["Backend/AutopilotMonitor.Functions/Services/Notifications/GenericJsonRenderer.cs"] = OutgoingPayload,
        ["Backend/AutopilotMonitor.Functions/Services/Notifications/SlackRenderer.cs"] = OutgoingPayload,
        ["Backend/AutopilotMonitor.Functions/Services/Notifications/TeamsWorkflowAdaptiveCardRenderer.cs"] = OutgoingPayload,

        ["Backend/AutopilotMonitor.Functions/Services/RuleTemplateInterpolator.cs"] = RuleTemplates,
    };

    /// <summary>The projects that may reference the Newtonsoft.Json package directly.</summary>
    private static readonly string[] ProjectsWithPackage =
    {
        "Agent/AutopilotMonitor.Agent.V2/AutopilotMonitor.Agent.V2.csproj",
        "Agent/AutopilotMonitor.Agent.V2.Core/AutopilotMonitor.Agent.V2.Core.csproj",
        "Agent/AutopilotMonitor.SummaryDialog/AutopilotMonitor.SummaryDialog.csproj",
        "Backend/AutopilotMonitor.Functions/AutopilotMonitor.Functions.csproj",
        "Shared/AutopilotMonitor.DecisionCore/AutopilotMonitor.DecisionCore.csproj",
    };

    private static readonly Regex NamespaceUse = new(@"\bNewtonsoft\.Json\b", RegexOptions.Compiled);

    private static readonly Regex PackageReference = new(
        @"<PackageReference\s+Include\s*=\s*""Newtonsoft\.Json""", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    [Fact]
    public void Newtonsoft_use_is_frozen_per_file()
    {
        var actual = ShippedSources()
            .Where(s => UsesNewtonsoft(s.Text))
            .Select(s => s.Relative)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var violations = new List<string>();
        foreach (var file in actual.OrderBy(f => f, StringComparer.Ordinal))
        {
            if (!Baseline.ContainsKey(file))
                violations.Add($"{file}: NEW use of Newtonsoft.Json — read and write JSON with System.Text.Json; the Newtonsoft baseline is frozen (D-420).");
        }
        foreach (var file in Baseline.Keys.OrderBy(f => f, StringComparer.Ordinal))
        {
            if (!actual.Contains(file))
                violations.Add($"{file}: on the baseline but no longer uses Newtonsoft.Json (or is gone) — remove its entry (ratchet down).");
        }

        Assert.True(violations.Count == 0, "Newtonsoft.Json baseline violated:\n  " + string.Join("\n  ", violations));
    }

    [Fact]
    public void Newtonsoft_package_only_in_known_projects()
    {
        var srcRoot = Path.Combine(FindRepoRoot(), "src");
        var actual = SourceFiles(srcRoot, "*.csproj", includeTestProjects: true)
            .Where(f => PackageReference.IsMatch(File.ReadAllText(f)))
            .Select(f => Relative(srcRoot, f))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var violations = actual.Except(ProjectsWithPackage, StringComparer.OrdinalIgnoreCase)
            .Select(p => $"{p}: NEW Newtonsoft.Json package reference — use System.Text.Json (D-420).")
            .Concat(ProjectsWithPackage.Except(actual, StringComparer.OrdinalIgnoreCase)
                .Select(p => $"{p}: listed but no longer references Newtonsoft.Json — remove it from ProjectsWithPackage (ratchet down)."))
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToList();

        Assert.True(violations.Count == 0, "Newtonsoft.Json package references violated:\n  " + string.Join("\n  ", violations));
    }

    [Theory]
    [InlineData("using Newtonsoft.Json;", true)]
    [InlineData("using Newtonsoft.Json.Linq;", true)]
    [InlineData("using JsonConvert = Newtonsoft.Json.JsonConvert;", true)]
    [InlineData("    catch (Newtonsoft.Json.JsonException) { }", true)]
    [InlineData("var dll = Path.Combine(dir, \"Newtonsoft.Json.dll\");", true)]
    [InlineData("var url = \"https://x\"; Newtonsoft.Json.Linq.JObject.Parse(s); // trailing", true)]
    [InlineData("var x = 1; // Newtonsoft.Json is not used here", false)]
    [InlineData("    /// <summary>Newtonsoft.Json defaults on both sides.</summary>", false)]
    [InlineData("// Newtonsoft.Json", false)]
    [InlineData("    /* Newtonsoft.Json */", false)]
    [InlineData("     * Newtonsoft.Json in a block comment", false)]
    [InlineData("var rule = await RequestBody.ReadNewtonsoftAsync<AnalyzeRule>(req);", false)]
    public void Use_detector_sees_code_but_not_comments(string line, bool expected)
        => Assert.Equal(expected, UsesNewtonsoft(line));

    private static bool UsesNewtonsoft(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var trimmed = raw.TrimStart();
            if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith("/*", StringComparison.Ordinal) || trimmed.StartsWith('*'))
                continue;
            var line = raw;
            var comment = line.IndexOf("//", StringComparison.Ordinal);
            if (comment >= 0 && !line[..comment].Contains('"'))
                line = line[..comment];
            if (NamespaceUse.IsMatch(line))
                return true;
        }
        return false;
    }

    private static IEnumerable<(string Relative, string Text)> ShippedSources()
    {
        var srcRoot = Path.Combine(FindRepoRoot(), "src");
        return SourceFiles(srcRoot, "*.cs", includeTestProjects: false)
            .Select(f => (Relative(srcRoot, f), File.ReadAllText(f)));
    }

    /// <summary>
    /// Walks <paramref name="root"/> without entering build output or package folders: the web and MCP
    /// node_modules alone would make a plain recursive enumeration take tens of seconds.
    /// </summary>
    private static IEnumerable<string> SourceFiles(string root, string pattern, bool includeTestProjects)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            foreach (var file in Directory.EnumerateFiles(dir, pattern))
                yield return file;
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                var name = Path.GetFileName(sub);
                if (SkippedFolders.Contains(name) || (!includeTestProjects && name.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase)))
                    continue;
                pending.Push(sub);
            }
        }
    }

    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", "node_modules", ".next" };

    private static string Relative(string root, string path)
        => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AutopilotMonitor.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
