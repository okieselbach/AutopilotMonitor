using System;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Tests.Orchestration;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.SystemSignals
{
    /// <summary>
    /// Backlog q8n — pins the two AccountSetup probes side by side.
    /// <para>
    /// <see cref="ProvisioningStatusTracker.HasAccountSetupActivity"/> keeps PRESENCE semantics:
    /// Windows writes <c>AccountSetupCategory.Status</c> with every subcategory <c>notStarted</c>
    /// before the Device-ESP page exits, and about one in five Classic sessions sees only that
    /// single Shell-Core 62407 for the whole enrollment — those resolve AccountSetup solely via
    /// the user-apps-settled synthesis, which needs the exit remembered as the edge. Tightening
    /// this property strands them (verified on 485 SkipUser=false sessions, 2026-09-01..04).
    /// </para>
    /// <para>
    /// <see cref="ProvisioningStatusTracker.HasAccountSetupProgress"/> is the additive PROGRESS
    /// probe for the User-ESP keep-awake host: true only once a subcategory left
    /// <c>notStarted</c>, so the host can tell the real user-ESP page exit (session a2256107:
    /// 09:47:12) from the Device→Account handoff exit (09:37:53) that released the hold early.
    /// </para>
    /// </summary>
    public sealed class ProvisioningStatusTrackerAccountSetupActivityTests
    {
        private static readonly DateTime Fixed = new DateTime(2026, 9, 4, 9, 37, 42, DateTimeKind.Utc);

        // Verbatim shape of the pre-written registry JSON seen at first_seen on 25H2/26200.
        private const string AllNotStartedJson = @"{
            ""categoryState"":""notStarted"",
            ""AccountSetup.WaitingForAadRegistrationSubcategory"":{""subcategoryState"":""notStarted""},
            ""AccountSetup.PrepareMultifactorAuth"":{""subcategoryState"":""notStarted""},
            ""AccountSetup.SecurityPoliciesSubcategory"":{""subcategoryState"":""notStarted""},
            ""AccountSetup.CertificatesSubcategory"":{""subcategoryState"":""notStarted""},
            ""AccountSetup.NetworkConnectionsSubcategory"":{""subcategoryState"":""notStarted""},
            ""AccountSetup.AppsSubcategory"":{""subcategoryState"":""notStarted""},
            ""AccountSetup.SendResultsToMdmServer"":{""subcategoryState"":""notStarted""}
        }";

        private const string FirstProgressJson = @"{
            ""categoryState"":""inProgress"",
            ""AccountSetup.WaitingForAadRegistrationSubcategory"":{""subcategoryState"":""succeeded"",""subcategoryStatusText"":""succeeded""},
            ""AccountSetup.PrepareMultifactorAuth"":{""subcategoryState"":""notStarted""},
            ""AccountSetup.SecurityPoliciesSubcategory"":{""subcategoryState"":""notStarted""},
            ""AccountSetup.CertificatesSubcategory"":{""subcategoryState"":""notStarted""},
            ""AccountSetup.NetworkConnectionsSubcategory"":{""subcategoryState"":""notStarted""},
            ""AccountSetup.AppsSubcategory"":{""subcategoryState"":""notStarted""},
            ""AccountSetup.SendResultsToMdmServer"":{""subcategoryState"":""notStarted""}
        }";

        private sealed class Fixture : IDisposable
        {
            public TempDirectory Tmp { get; } = new TempDirectory();
            public FakeSignalIngressSink Sink { get; } = new FakeSignalIngressSink();
            public ProvisioningStatusTracker Tracker { get; }

            public Fixture()
            {
                var clock = new VirtualClock(Fixed);
                var post = new InformationalEventPost(Sink, clock);
                var logger = new AgentLogger(Tmp.Path, AgentLogLevel.Info);
                Tracker = new ProvisioningStatusTracker(
                    sessionId: "S1",
                    tenantId: "T1",
                    post: post,
                    logger: logger);
            }

            public void Dispose()
            {
                Tracker.Dispose();
                Tmp.Dispose();
            }
        }

        [Fact]
        public void NothingTracked_NeitherActivityNorProgress()
        {
            using var f = new Fixture();
            Assert.False(f.Tracker.HasAccountSetupActivity);
            Assert.False(f.Tracker.HasAccountSetupProgress);
        }

        [Fact]
        public void PreWrittenAllNotStartedJson_IsActivity_ButNotProgress()
        {
            // The registry JSON exists (7 subcategories) but nothing has started — the state at
            // the intermediate Device-ESP exit. Activity (presence) must stay true so the
            // completion guards keep remembering single-exit sessions; progress must be false so
            // the keep-awake host does not treat this exit as the user-ESP page exit.
            using var f = new Fixture();
            f.Tracker.ProcessCategoryStatusForTest("AccountSetupCategory.Status", AllNotStartedJson);

            Assert.True(f.Tracker.HasAccountSetupActivity);
            Assert.False(f.Tracker.HasAccountSetupProgress);
        }

        [Fact]
        public void FirstSubcategoryLeavingNotStarted_IsProgress()
        {
            using var f = new Fixture();
            f.Tracker.ProcessCategoryStatusForTest("AccountSetupCategory.Status", AllNotStartedJson);
            f.Tracker.ProcessCategoryStatusForTest("AccountSetupCategory.Status", FirstProgressJson);

            Assert.True(f.Tracker.HasAccountSetupActivity);
            Assert.True(f.Tracker.HasAccountSetupProgress);
        }

        [Theory]
        [InlineData("inProgress")]
        [InlineData("in_progress")]
        [InlineData("failed")]
        [InlineData("notRequired")]
        public void AnyNonNotStartedState_IsProgress(string state)
        {
            using var f = new Fixture();
            f.Tracker.ProcessCategoryStatusForTest("AccountSetupCategory.Status",
                "{\"categoryState\":\"inProgress\"," +
                "\"AccountSetup.AppsSubcategory\":{\"subcategoryState\":\"" + state + "\"}," +
                "\"AccountSetup.CertificatesSubcategory\":{\"subcategoryState\":\"notStarted\"}}");

            Assert.True(f.Tracker.HasAccountSetupProgress);
        }

        [Fact]
        public void DeviceSetupProgress_DoesNotCountAsAccountSetupProgress()
        {
            // Adversarial: a fully progressed DeviceSetup category must not leak into the
            // AccountSetup answer.
            using var f = new Fixture();
            f.Tracker.ProcessCategoryStatusForTest("DeviceSetupCategory.Status",
                "{\"categoryState\":\"succeeded\",\"DeviceSetup.AppsSubcategory\":{\"subcategoryState\":\"succeeded\"}}");
            f.Tracker.ProcessCategoryStatusForTest("AccountSetupCategory.Status", AllNotStartedJson);

            Assert.False(f.Tracker.HasAccountSetupProgress);
        }
    }
}
