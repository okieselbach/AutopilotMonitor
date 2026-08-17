#nullable enable
using System;
using System.Collections.Generic;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.SystemSignals
{
    /// <summary>
    /// Selection/normalization logic of <see cref="ImeMsiInstallSourceProbe"/> — the registry
    /// walk itself is not unit-testable (live HKLM), so tests cover the pure pieces:
    /// <c>SelectBestMatch</c>, <c>VersionsEqual</c>, <c>IsImeMsiUrl</c>.
    /// </summary>
    public class ImeMsiInstallSourceProbeTests
    {
        private const string ImeProductCode = "{6A7DFC50-0395-4E1E-BF84-ED1404E72051}";
        private const string ImeUrl = "https://imeswdb-afd-secondary.manage.microsoft.com/IntuneWindowsAgent.msi";

        private static ImeMsiInstallSourceProbe.MsiEntry Entry(
            string productCode = ImeProductCode,
            string version = "1.104.102.0",
            string url = ImeUrl)
            => new ImeMsiInstallSourceProbe.MsiEntry(productCode, version, url);

        // -- SelectBestMatch -----------------------------------------------------------

        [Fact]
        public void SelectBestMatch_exact_product_version_wins()
        {
            var result = ImeMsiInstallSourceProbe.SelectBestMatch(
                new List<ImeMsiInstallSourceProbe.MsiEntry>
                {
                    Entry(productCode: "{OTHER}", version: "9.9.9.9", url: "https://x/Other.msi"),
                    Entry(),
                },
                "1.104.102.0");

            Assert.True(result.HasData);
            Assert.True(result.MatchedByVersion);
            Assert.Equal(ImeProductCode, result.ProductCode);
            Assert.Equal("1.104.102.0", result.ProductVersion);
            Assert.Equal(ImeUrl, result.DownloadUrl);
        }

        [Fact]
        public void SelectBestMatch_normalized_version_matches_trailing_zero()
        {
            var result = ImeMsiInstallSourceProbe.SelectBestMatch(
                new List<ImeMsiInstallSourceProbe.MsiEntry> { Entry(version: "1.104.102") },
                "1.104.102.0");

            Assert.True(result.HasData);
            Assert.True(result.MatchedByVersion);
        }

        [Fact]
        public void SelectBestMatch_version_mismatch_falls_back_to_ime_msi_filename()
        {
            // IME self-updated between MSI enforcement and log line — versions drift, but the
            // enforced IntuneWindowsAgent.msi entry is still the install source.
            var result = ImeMsiInstallSourceProbe.SelectBestMatch(
                new List<ImeMsiInstallSourceProbe.MsiEntry>
                {
                    Entry(productCode: "{OTHER}", version: "5.0.0.0", url: "https://x/SomeLobApp.msi"),
                    Entry(version: "1.103.100.0"),
                },
                "1.104.102.0");

            Assert.True(result.HasData);
            Assert.False(result.MatchedByVersion);
            Assert.Equal("1.103.100.0", result.ProductVersion);
            Assert.Equal(ImeUrl, result.DownloadUrl);
        }

        [Fact]
        public void SelectBestMatch_version_match_beats_filename_match_on_other_entry()
        {
            // A stale IntuneWindowsAgent.msi row must not shadow the entry whose
            // ProductVersion equals the running build.
            var result = ImeMsiInstallSourceProbe.SelectBestMatch(
                new List<ImeMsiInstallSourceProbe.MsiEntry>
                {
                    Entry(productCode: "{STALE}", version: "1.103.100.0"),
                    Entry(productCode: "{CURRENT}", version: "1.104.102.0", url: "https://y/IntuneWindowsAgent.msi"),
                },
                "1.104.102.0");

            Assert.True(result.MatchedByVersion);
            Assert.Equal("{CURRENT}", result.ProductCode);
        }

        [Fact]
        public void SelectBestMatch_no_plausible_entry_returns_empty()
        {
            var result = ImeMsiInstallSourceProbe.SelectBestMatch(
                new List<ImeMsiInstallSourceProbe.MsiEntry>
                {
                    Entry(productCode: "{OTHER}", version: "5.0.0.0", url: "https://x/SomeLobApp.msi"),
                },
                "1.104.102.0");

            Assert.False(result.HasData);
        }

        [Fact]
        public void SelectBestMatch_empty_or_null_candidates_return_empty()
        {
            Assert.False(ImeMsiInstallSourceProbe.SelectBestMatch(
                new List<ImeMsiInstallSourceProbe.MsiEntry>(), "1.104.102.0").HasData);
            Assert.False(ImeMsiInstallSourceProbe.SelectBestMatch(
                null, "1.104.102.0").HasData);
        }

        [Fact]
        public void SelectBestMatch_null_registry_values_do_not_throw()
        {
            var result = ImeMsiInstallSourceProbe.SelectBestMatch(
                new List<ImeMsiInstallSourceProbe.MsiEntry>
                {
                    new ImeMsiInstallSourceProbe.MsiEntry("{PARTIAL}", null, null),
                    Entry(),
                },
                "1.104.102.0");

            Assert.True(result.HasData);
            Assert.Equal(ImeProductCode, result.ProductCode);
        }

        // -- VersionsEqual ---------------------------------------------------------------

        [Theory]
        [InlineData("1.104.102.0", "1.104.102.0", true)]
        [InlineData("1.104.102", "1.104.102.0", true)]
        [InlineData("1.104.102.0", "1.104.102", true)]
        [InlineData(" 1.104.102.0 ", "1.104.102.0", true)]
        [InlineData("1.104.102.0", "1.104.103.0", false)]
        [InlineData("1.104.102.0", "1.104.102.1", false)]
        [InlineData("1.104", "1.104.102.0", false)]
        [InlineData("abc", "abc", true)]     // non-numeric: whole-string equality
        [InlineData("abc", "abd", false)]    // non-numeric components never fuzzy-match
        [InlineData(null, "1.0", false)]
        [InlineData("1.0", null, false)]
        [InlineData("", "", false)]
        public void VersionsEqual_cases(string? a, string? b, bool expected)
        {
            Assert.Equal(expected, ImeMsiInstallSourceProbe.VersionsEqual(a, b));
        }

        // -- IsImeMsiUrl -------------------------------------------------------------------

        [Theory]
        [InlineData(ImeUrl, true)]
        [InlineData("https://x/intunewindowsagent.MSI", true)]                  // case-insensitive
        [InlineData("https://x/IntuneWindowsAgent.msi?sv=abc&sig=def", true)]   // query ignored
        [InlineData("IntuneWindowsAgent.msi", true)]                            // bare filename
        [InlineData("https://x/NotTheAgent.msi", false)]
        [InlineData("https://x/IntuneWindowsAgent.msi.bak", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void IsImeMsiUrl_cases(string? url, bool expected)
        {
            Assert.Equal(expected, ImeMsiInstallSourceProbe.IsImeMsiUrl(url));
        }

        // -- ScopedOverride ---------------------------------------------------------------

        [Fact]
        public void ScopedOverride_restores_previous_override_on_dispose()
        {
            // Assembly default (TestAssemblyInit) forces Empty; a nested scope must restore it.
            Func<string, AgentLogger, ImeMsiInstallSource> before =
                ImeMsiInstallSourceProbe.TestOverride;
            using (new ImeMsiInstallSourceProbe.ScopedOverride((_, _) =>
                       ImeMsiInstallSourceProbe.SelectBestMatch(
                           new List<ImeMsiInstallSourceProbe.MsiEntry> { Entry() }, "1.104.102.0")))
            {
                Assert.True(ImeMsiInstallSourceProbe.Read("1.104.102.0").HasData);
            }
            Assert.Same(before, ImeMsiInstallSourceProbe.TestOverride);
        }
    }
}
