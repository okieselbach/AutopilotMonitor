using System.Collections.Generic;
using System.IO;
using AutopilotMonitor.Agent.V2.Core.Security;
using AutopilotMonitor.Shared;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Security
{
    /// <summary>
    /// The decision over what <c>Test-FileCatalog -Detailed</c> reports. The runspace itself is
    /// exercised on a lab device (plan agent-payload-catalog, V1/V3), never in the test host.
    /// </summary>
    public sealed class PayloadCatalogVerifierTests
    {
        private const string OurSubject = "CN=glueckkanja AG, O=glueckkanja AG, L=Offenbach, S=Hessen, C=DE";

        private static Dictionary<string, string> Files(params string[] namesAndHashes)
        {
            var map = new Dictionary<string, string>();
            for (var i = 0; i < namesAndHashes.Length; i += 2) map[namesAndHashes[i]] = namesAndHashes[i + 1];
            return map;
        }

        private static PayloadCatalogFacts Facts(
            string status = "Valid", string? signatureStatus = "Valid", string? subject = OurSubject,
            Dictionary<string, string>? catalog = null, Dictionary<string, string>? path = null)
            => new PayloadCatalogFacts(status, signatureStatus, subject,
                catalog ?? Files("a.dll", "AA"), path ?? Files("a.dll", "AA"));

        private static PayloadCatalogVerdict Evaluate(PayloadCatalogFacts facts)
            => PayloadCatalogVerifier.Evaluate(facts, Constants.CodeSigningPublisherPattern);

        [Fact]
        public void Signed_by_us_and_matching_is_accepted()
        {
            var verdict = Evaluate(Facts());

            Assert.True(verdict.Accepted);
            Assert.Null(verdict.Reason);
        }

        [Fact]
        public void Matching_hashes_under_an_unsigned_catalog_are_rejected()
        {
            // Status covers the hashes only: an unsigned catalog over matching files reports Valid.
            var verdict = Evaluate(Facts(signatureStatus: "NotSigned", subject: null));

            Assert.False(verdict.Accepted);
            Assert.Contains("catalog signature status NotSigned", verdict.Reason);
        }

        [Fact]
        public void A_validly_signed_catalog_from_another_publisher_is_rejected()
        {
            var verdict = Evaluate(Facts(subject: "CN=Contoso Ltd, O=Contoso Ltd, C=US"));

            Assert.False(verdict.Accepted);
            Assert.Contains("does not match", verdict.Reason);
        }

        [Fact]
        public void Missing_signature_details_are_a_rejection_not_a_pass()
        {
            Assert.False(Evaluate(Facts(signatureStatus: null, subject: null)).Accepted);
            Assert.False(Evaluate(Facts(subject: "")).Accepted);
        }

        [Fact]
        public void A_differing_payload_names_missing_extra_and_changed_files()
        {
            var verdict = Evaluate(Facts(
                status: "ValidationFailed",
                catalog: Files("a.dll", "AA", "b.dll", "BB", "c.config", "CC"),
                path: Files("a.dll", "AA", "b.dll", "XX", "evil.dll", "EE")));

            Assert.False(verdict.Accepted);
            Assert.Contains("missing: c.config", verdict.Reason);
            Assert.Contains("extra: evil.dll", verdict.Reason);
            Assert.Contains("changed: b.dll", verdict.Reason);
        }

        [Fact]
        public void Long_difference_lists_are_capped()
        {
            var path = Files("a.dll", "AA");
            for (var i = 0; i < 12; i++) path[$"extra{i:00}.txt"] = "00";

            var verdict = Evaluate(Facts(status: "ValidationFailed", path: path));

            Assert.Contains("(+7 more)", verdict.Reason);
        }

        [Fact]
        public void Signature_and_payload_problems_are_both_reported()
        {
            var verdict = Evaluate(Facts(status: "ValidationFailed", signatureStatus: "HashMismatch", subject: null,
                path: Files("a.dll", "AA", "x.dll", "11")));

            Assert.Contains("catalog signature status HashMismatch", verdict.Reason);
            Assert.Contains("extra: x.dll", verdict.Reason);
        }

        [Theory]
        [InlineData(OurSubject, true)]
        [InlineData("cn=glueckkanja ag, o=glueckkanja ag, c=de", true)]   // -like is case-insensitive
        [InlineData("CN=x, O=glueckkanja AG Holding, C=DE", true)]        // the pattern is open-ended by design
        [InlineData("CN=glueckkanja AG, O=Contoso, C=DE", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void Publisher_pattern_behaves_like_powershell_like(string? subject, bool expected)
        {
            Assert.Equal(expected, PayloadCatalogVerifier.MatchesPublisherPattern(subject, Constants.CodeSigningPublisherPattern));
        }

        [Fact]
        public void Regex_metacharacters_in_the_pattern_are_literal()
        {
            Assert.True(PayloadCatalogVerifier.MatchesPublisherPattern("O=Json.NET (.NET Foundation)", "*O=Json.NET (.NET Foundation)*"));
            Assert.False(PayloadCatalogVerifier.MatchesPublisherPattern("O=JsonXNET (.NET Foundation)", "*O=Json.NET (.NET Foundation)*"));
        }

        [Fact]
        public void A_payload_without_catalog_is_rejected_before_any_runspace_starts()
        {
            var dir = Path.Combine(Path.GetTempPath(), "apm-catalog-test-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllText(Path.Combine(dir, "readme.txt"), "x");

                var verdict = PayloadCatalogVerifier.Verify(dir);

                Assert.False(verdict.Accepted);
                Assert.Contains("missing", verdict.Reason);
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
