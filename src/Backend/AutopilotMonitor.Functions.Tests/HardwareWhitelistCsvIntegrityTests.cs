using System.Linq;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Functions.Tests
{
    /// <summary>
    /// The hardware whitelist is stored as one CSV and split at enforcement time. A value that
    /// carries the delimiter (e.g. an attacker-chosen distress-signal manufacturer "Dell Inc.,*")
    /// must never be able to widen the gate: the server rejects malformed lists and the parser
    /// yields exactly the trimmed, non-empty items.
    /// </summary>
    public class HardwareWhitelistCsvIntegrityTests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("*")]
        [InlineData("Dell*,HP*,Lenovo*,Microsoft Corporation")]
        [InlineData("Dell Inc.?*")]
        [InlineData(" Dell* , HP* ")]
        public void ValidateHardwareWhitelist_WellFormed_ReturnsNull(string? csv)
        {
            Assert.Null(TenantConfigValidation.ValidateHardwareWhitelist(csv));
        }

        [Theory]
        [InlineData("Dell Inc.,")]
        [InlineData(",Dell*")]
        [InlineData("Dell*,,HP*")]
        [InlineData("Dell*, ,HP*")]
        [InlineData(",")]
        public void ValidateHardwareWhitelist_BlankItem_Rejected(string csv)
        {
            var error = TenantConfigValidation.ValidateHardwareWhitelist(csv);
            Assert.NotNull(error);
            Assert.Contains("must not be empty", error);
        }

        [Fact]
        public void ValidateHardwareWhitelist_ControlCharacter_Rejected()
        {
            var csv = "Dell*" + (char)1 + ",HP*";
            Assert.Contains("control characters", TenantConfigValidation.ValidateHardwareWhitelist(csv));
        }

        [Fact]
        public void ValidateHardwareWhitelist_OverlongEntry_Rejected()
        {
            Assert.Contains("128 characters",
                TenantConfigValidation.ValidateHardwareWhitelist(new string('a', 129)));
        }

        [Fact]
        public void ValidateHardwareWhitelist_TooManyEntries_Rejected()
        {
            var csv = string.Join(",", Enumerable.Repeat("x", 201));
            Assert.Contains("200 entries", TenantConfigValidation.ValidateHardwareWhitelist(csv));
        }

        [Fact]
        public void ValidateModel_RejectsMalformedWhitelists_WithFieldSpecificMessage()
        {
            var existing = TenantConfiguration.CreateDefault("tenant");

            var badMfr = TenantConfiguration.CreateDefault("tenant");
            badMfr.ManufacturerWhitelist = "Dell*,Dell Inc.,";
            Assert.StartsWith("Invalid manufacturer whitelist:",
                TenantConfigValidation.ValidateModel(badMfr, existing, isGlobalAdmin: false));

            var badModel = TenantConfiguration.CreateDefault("tenant");
            badModel.ModelWhitelist = "Latitude*,,";
            Assert.StartsWith("Invalid model whitelist:",
                TenantConfigValidation.ValidateModel(badModel, existing, isGlobalAdmin: false));

            Assert.Null(TenantConfigValidation.ValidateModel(
                TenantConfiguration.CreateDefault("tenant"), existing, isGlobalAdmin: false));
        }

        [Fact]
        public void ParseWhitelist_TrimsEntries_SoEditorViewMatchesEnforcement()
        {
            // The web editor displays trimmed items; enforcement must match the same patterns.
            Assert.Equal(new[] { "Dell*", "HP*" }, TenantConfiguration.ParseWhitelist("Dell*, HP*"));
            Assert.Equal(new[] { "*" }, TenantConfiguration.ParseWhitelist(" "));
        }

        [Fact]
        public void NeutralizedDistressValue_MatchesOnlyItself_NotEveryManufacturer()
        {
            // "Dell Inc.,*" neutralized by the web helper to the single pattern "Dell Inc.?*".
            var whitelist = TenantConfiguration.ParseWhitelist("Dell*,Dell Inc.?*");
            Assert.DoesNotContain("*", whitelist);

            Assert.False(HardwareWhitelistValidator.ValidateHardware("Contoso", "X1", whitelist, new[] { "*" }).IsValid);
            Assert.True(HardwareWhitelistValidator.ValidateHardware("Dell Inc.,*", "X1", whitelist, new[] { "*" }).IsValid);
        }
    }
}
