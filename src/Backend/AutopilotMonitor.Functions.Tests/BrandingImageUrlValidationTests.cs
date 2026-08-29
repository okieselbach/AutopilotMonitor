using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Functions.Tests
{
    /// <summary>
    /// The enrollment-summary branding image URL is delivered verbatim to every enrolling device,
    /// fetched from the signed-in user's session and spliced into the summary-dialog command line.
    /// The server must therefore only ever persist a plain absolute HTTPS URL — a value with an
    /// embedded quote or a non-HTTPS scheme must fail the config PUT/PATCH.
    /// </summary>
    public class BrandingImageUrlValidationTests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("https://cdn.example/logo.png")]
        [InlineData("https://cdn.example:8443/assets/logo.png?v=2&x=y")]
        [InlineData("https://cdn.example/logo%20one.png")]
        public void ValidateBrandingImageUrl_WellFormed_ReturnsNull(string? url)
        {
            Assert.Null(TenantConfigValidation.ValidateBrandingImageUrl(url));
        }

        [Theory]
        [InlineData("x\" --status-file \"C:\\some\\file\" --timeout \"0", "quotes")]
        [InlineData("https://cdn.example/logo.png\"", "quotes")]
        [InlineData("https://cdn.example/lo'go.png", "quotes")]
        [InlineData("https://cdn.example/logo.png --cleanup", "whitespace")]
        [InlineData("https://cdn.example/logo.png\r\n", "whitespace")]
        [InlineData("http://cdn.example/logo.png", "HTTPS")]
        [InlineData("file:///C:/Windows/logo.png", "HTTPS")]
        [InlineData("ftp://cdn.example/logo.png", "HTTPS")]
        [InlineData("cdn.example/logo.png", "absolute URL")]
        [InlineData("https://localhost/logo.png", "localhost")]
        [InlineData("https://10.0.0.1/logo.png", "IP address")]
        public void ValidateBrandingImageUrl_Malformed_Rejected(string url, string expectedFragment)
        {
            var error = TenantConfigValidation.ValidateBrandingImageUrl(url);
            Assert.NotNull(error);
            Assert.Contains(expectedFragment, error);
        }

        [Fact]
        public void ValidateBrandingImageUrl_Overlong_Rejected()
        {
            var url = "https://cdn.example/" + new string('a', TenantConfigValidation.MaxBrandingImageUrlLength);
            Assert.Contains("characters", TenantConfigValidation.ValidateBrandingImageUrl(url));
        }

        [Fact]
        public void ValidateModel_RejectsInjectedBrandingUrl()
        {
            var existing = new TenantConfiguration();
            var candidate = new TenantConfiguration
            {
                EnrollmentSummaryBrandingImageUrl = "x\" --status-file \"C:\\some\\file\" --timeout \"0",
            };

            var error = TenantConfigValidation.ValidateModel(candidate, existing, isGlobalAdmin: true);

            Assert.NotNull(error);
            Assert.StartsWith("Invalid enrollment summary branding image URL", error);
        }
    }
}
