using System.Collections.Generic;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring
{
    /// <summary>
    /// The ESP phase a report names, derived from the three ESP registry values. Both value shapes
    /// Windows writes are covered: flat status texts (older builds) and nested subcategory objects.
    /// </summary>
    public sealed class EspPhaseSnapshotTests
    {
        private const string DoneFlag = "{\"categorySucceeded\":true}";
        private const string FailedFlag = "{\"categorySucceeded\":false}";
        private const string NestedRunning =
            "{\"SecurityPoliciesSubcategory\":{\"subcategoryState\":\"succeeded\",\"subcategoryStatusText\":\"Complete\"}," +
            "\"AppsSubcategory\":{\"subcategoryState\":\"inProgress\",\"subcategoryStatusText\":\"Installing (1 of 3)\"}}";
        private const string NestedAllDoneNoFlag =
            "{\"SecurityPoliciesSubcategory\":{\"subcategoryState\":\"succeeded\"},\"AppsSubcategory\":{\"subcategoryState\":\"notRequired\"}}";
        private const string NestedFailedNoFlag =
            "{\"SecurityPoliciesSubcategory\":{\"subcategoryState\":\"succeeded\"},\"AppsSubcategory\":{\"subcategoryState\":\"failed\"}}";
        private const string FlatRunning = "{\"CertificatesSubcategory\":\"Certificates (0 of 1 applied)\",\"AppsSubcategory\":\"Identifying\"}";

        private static string Derive(string? preparation, string? deviceSetup, string? accountSetup)
            => EspPhaseSnapshot.Derive(new List<string> { preparation!, deviceSetup!, accountSetup! });

        [Fact]
        public void No_value_is_none()
        {
            Assert.Equal("none", Derive(null, null, null));
        }

        [Fact]
        public void Running_category_names_its_phase()
        {
            Assert.Equal("device_preparation", Derive(NestedRunning, null, null));
            Assert.Equal("device_setup", Derive(DoneFlag, NestedRunning, null));
            Assert.Equal("account_setup", Derive(DoneFlag, DoneFlag, NestedRunning));
        }

        [Fact]
        public void Flat_status_texts_are_read_too()
        {
            Assert.Equal("device_setup", Derive(DoneFlag, FlatRunning, null));
        }

        [Fact]
        public void Done_categories_name_the_last_one_reached()
        {
            Assert.Equal("device_preparation_done", Derive(DoneFlag, null, null));
            Assert.Equal("device_setup_done", Derive(DoneFlag, DoneFlag, null));
            Assert.Equal("esp_done", Derive(DoneFlag, DoneFlag, DoneFlag));
        }

        [Fact]
        public void All_subcategories_done_counts_as_done_without_the_flag()
        {
            // Some builds never set categorySucceeded although every subcategory finished.
            Assert.Equal("esp_done", Derive(DoneFlag, NestedAllDoneNoFlag, NestedAllDoneNoFlag));
        }

        [Fact]
        public void A_failure_wins_over_everything_after_it()
        {
            Assert.Equal("device_setup_failed", Derive(DoneFlag, FailedFlag, NestedRunning));
            Assert.Equal("account_setup_failed", Derive(DoneFlag, DoneFlag, NestedFailedNoFlag));
        }

        [Fact]
        public void Unparseable_value_counts_as_running()
        {
            Assert.Equal("device_setup", Derive(DoneFlag, "{not json", null));
        }

        [Fact]
        public void Read_on_this_machine_returns_a_known_token()
        {
            var phase = EspPhaseSnapshot.Read();
            Assert.Matches("^[a-z_]+$", phase);
        }
    }
}
