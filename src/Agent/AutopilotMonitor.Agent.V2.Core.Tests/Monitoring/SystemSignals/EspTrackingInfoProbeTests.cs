#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.DeviceInfo;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Tests.Orchestration;
using AutopilotMonitor.DecisionCore.Engine;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.SystemSignals
{
    /// <summary>
    /// Session a4537c36 — pure LocURI extraction helpers of <see cref="EspTrackingInfoProbe"/>.
    /// Registry semantics per Microsoft's Get-AutopilotDiagnostics.ps1 (PSGallery 5.6): value
    /// NAMES under the ESPTrackingInfo\Diagnostics category subkeys are CSP LocURIs carrying
    /// MSI ProductCodes / PackageFamilyNames / Intune app GUIDs (Sidecar).
    /// </summary>
    public sealed class EspTrackingInfoProbeExtractionTests
    {
        // ---------------------------------------------------------------- MSI

        [Fact]
        public void Msi_url_encoded_braced_product_code_is_extracted_uppercase()
        {
            var ok = EspTrackingInfoProbe.TryExtractMsiProductCode(
                "./Device/Vendor/MSFT/EnterpriseDesktopAppManagement/MSI/%7B23170f69-40c1-2702-1900-000001000000%7D/Status",
                out var code);

            Assert.True(ok);
            Assert.Equal("{23170F69-40C1-2702-1900-000001000000}", code);
        }

        [Fact]
        public void Msi_plain_braced_product_code_is_extracted()
        {
            var ok = EspTrackingInfoProbe.TryExtractMsiProductCode(
                "./Device/Vendor/MSFT/EnterpriseDesktopAppManagement/MSI/{ABCDEF01-2345-6789-ABCD-EF0123456789}/DownloadInstall",
                out var code);

            Assert.True(ok);
            Assert.Equal("{ABCDEF01-2345-6789-ABCD-EF0123456789}", code);
        }

        [Fact]
        public void Msi_bare_guid_gets_braces_added()
        {
            var ok = EspTrackingInfoProbe.TryExtractMsiProductCode(
                "./User/Vendor/MSFT/EnterpriseDesktopAppManagement/MSI/abcdef01-2345-6789-abcd-ef0123456789/Status",
                out var code);

            Assert.True(ok);
            Assert.Equal("{ABCDEF01-2345-6789-ABCD-EF0123456789}", code);
        }

        [Fact]
        public void Msi_rejects_non_desktop_app_management_uris()
        {
            Assert.False(EspTrackingInfoProbe.TryExtractMsiProductCode(
                "./Device/Vendor/MSFT/Policy/{ABCDEF01-2345-6789-ABCD-EF0123456789}", out _));
            Assert.False(EspTrackingInfoProbe.TryExtractMsiProductCode(
                "./Device/Vendor/MSFT/EnterpriseDesktopAppManagement/MSI/not-a-guid/Status", out _));
            Assert.False(EspTrackingInfoProbe.TryExtractMsiProductCode(null, out _));
        }

        // ---------------------------------------------------------------- Modern / PFN

        [Fact]
        public void Modern_pfn_is_extracted_from_appstore_segment()
        {
            var ok = EspTrackingInfoProbe.TryExtractModernAppPfn(
                "./User/Vendor/MSFT/EnterpriseModernAppManagement/AppManagement/AppStore/Microsoft.CompanyPortal_8wekyb3d8bbwe/HostedInstall",
                out var pfn);

            Assert.True(ok);
            Assert.Equal("Microsoft.CompanyPortal_8wekyb3d8bbwe", pfn);
        }

        [Fact]
        public void Modern_rejects_uris_without_pfn_shaped_segment()
        {
            // Segment after AppStore without the "_publisherhash" shape is a CSP verb, not a PFN.
            Assert.False(EspTrackingInfoProbe.TryExtractModernAppPfn(
                "./User/Vendor/MSFT/EnterpriseModernAppManagement/AppManagement/AppStore/ReleaseManagement", out _));
            Assert.False(EspTrackingInfoProbe.TryExtractModernAppPfn(
                "./Device/Vendor/MSFT/EnterpriseDesktopAppManagement/MSI/{ABCDEF01-2345-6789-ABCD-EF0123456789}/Status", out _));
            Assert.False(EspTrackingInfoProbe.TryExtractModernAppPfn(string.Empty, out _));
        }

        // ---------------------------------------------------------------- Sidecar / Win32

        [Fact]
        public void Sidecar_guid_is_normalized_to_lowercase_dashed()
        {
            var ok = EspTrackingInfoProbe.TryExtractSidecarAppId(
                "./Device/Vendor/MSFT/Sidecar/Policies/C85B8588-6ACF-41EA-A728-3D489AD3DD9D",
                out var appId, out var isUserScoped);

            Assert.True(ok);
            // Same shape as AppPackageState.Id (IME tracker) — prepared for espBlocking matching.
            Assert.Equal("c85b8588-6acf-41ea-a728-3d489ad3dd9d", appId);
            Assert.False(isUserScoped);
        }

        [Fact]
        public void Sidecar_user_locuri_is_flagged_user_scoped()
        {
            var ok = EspTrackingInfoProbe.TryExtractSidecarAppId(
                "./User/Vendor/MSFT/Sidecar/Policies/3fec52db-7dc1-4327-bfd4-6cb6aca88b3a",
                out var appId, out var isUserScoped);

            Assert.True(ok);
            Assert.Equal("3fec52db-7dc1-4327-bfd4-6cb6aca88b3a", appId);
            Assert.True(isUserScoped);
        }

        [Fact]
        public void Sidecar_rejects_names_without_guid()
        {
            Assert.False(EspTrackingInfoProbe.TryExtractSidecarAppId("./Device/Vendor/MSFT/Sidecar/AllowStandardUserElevation", out _, out _));
            Assert.False(EspTrackingInfoProbe.TryExtractSidecarAppId(null, out _, out _));
        }

        // ---------------------------------------------------------------- cap contract

        [Fact]
        public void Capped_sorts_and_limits_to_max_per_category()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < EspTrackingInfoProbe.MaxIdsPerCategory + 1; i++)
                set.Add($"id-{i:D3}");

            var capped = EspTrackingInfoProbe.Capped(set);

            Assert.Equal(EspTrackingInfoProbe.MaxIdsPerCategory, capped.Count);
            Assert.Equal("id-000", capped[0]);
            // The uncapped total lives on the snapshot counts — set retains all entries.
            Assert.Equal(EspTrackingInfoProbe.MaxIdsPerCategory + 1, set.Count);
        }
    }

    /// <summary>
    /// esp_config_detected event surface: the tracking lists are attached when the probe has
    /// data and fully omitted when the Diagnostics key is absent. Static probe overrides →
    /// serial collection (same contract as <c>EnrollmentOrchestratorEspConfigBootstrapTests</c>).
    /// </summary>
    [Collection("SerialThreading")]
    public sealed class EspConfigDetectedTrackingFieldsTests : IDisposable
    {
        private static readonly DateTime At = new DateTime(2026, 7, 10, 14, 0, 0, DateTimeKind.Utc);

        private readonly TempDirectory _tmp = new TempDirectory();

        public void Dispose() => _tmp.Dispose();

        private (DeviceInfoCollector sut, FakeSignalIngressSink sink) BuildCollector()
        {
            var logger = new AgentLogger(_tmp.Path, AgentLogLevel.Info);
            var sink = new FakeSignalIngressSink();
            var post = new InformationalEventPost(sink, new VirtualClock(At));
            return (new DeviceInfoCollector("S1", "T1", post, logger), sink);
        }

        private static EspTrackingInfoSnapshot PopulatedTracking() => new EspTrackingInfoSnapshot(
            msiProductCodes: new[] { "{23170F69-40C1-2702-1900-000001000000}" },
            modernAppPfns: new[] { "Microsoft.CompanyPortal_8wekyb3d8bbwe" },
            win32AppIds: new[] { "c85b8588-6acf-41ea-a728-3d489ad3dd9d", "db7a772e-c41a-4c18-80bf-06c0fdf3396a" },
            userWin32AppIds: new[] { "db7a772e-c41a-4c18-80bf-06c0fdf3396a" },
            msiCount: 1,
            modernCount: 1,
            win32Count: 2);

        private static FakeSignalIngressSink.PostedSignal SingleEspConfigEvent(FakeSignalIngressSink sink) =>
            sink.Posted.Single(p =>
                p.Payload != null
                && p.Payload.TryGetValue(SignalPayloadKeys.EventType, out var et)
                && et == "esp_config_detected");

        [Fact]
        public void Tracking_lists_are_attached_when_probe_has_data()
        {
            using var _skip = new EspSkipConfigurationProbe.ScopedFullOverride(
                _ => new EspFirstSyncSnapshot(skipUser: false, skipDevice: false, blockInStatusPage: 3, syncFailureTimeoutMinutes: 45));
            using var _tracking = new EspTrackingInfoProbe.ScopedOverride(_ => PopulatedTracking());
            var (sut, sink) = BuildCollector();

            sut.CollectEspConfiguration();

            var evt = SingleEspConfigEvent(sink);
            var data = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object>>(evt.TypedPayload);
            var win32 = Assert.IsAssignableFrom<IReadOnlyList<string>>(data["espTrackedWin32AppIds"]);
            Assert.Equal(2, win32.Count);
            var userWin32 = Assert.IsAssignableFrom<IReadOnlyList<string>>(data["espTrackedUserWin32AppIds"]);
            Assert.Equal("db7a772e-c41a-4c18-80bf-06c0fdf3396a", Assert.Single(userWin32));
            Assert.Equal(2, data["espTrackedWin32Count"]);
            Assert.Equal(1, data["espTrackedMsiCount"]);
            Assert.Equal(1, data["espTrackedModernCount"]);
            Assert.Contains("TrackedApps(win32=2, msi=1, modern=1)", evt.Payload![SignalPayloadKeys.Message]);
        }

        [Fact]
        public void Tracking_keys_are_fully_omitted_when_diagnostics_key_absent()
        {
            using var _skip = new EspSkipConfigurationProbe.ScopedFullOverride(
                _ => new EspFirstSyncSnapshot(skipUser: false, skipDevice: false, blockInStatusPage: 3, syncFailureTimeoutMinutes: 45));
            using var _tracking = new EspTrackingInfoProbe.ScopedOverride(_ => EspTrackingInfoSnapshot.Empty);
            var (sut, sink) = BuildCollector();

            sut.CollectEspConfiguration();

            var evt = SingleEspConfigEvent(sink);
            var data = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object>>(evt.TypedPayload);
            Assert.DoesNotContain("espTrackedWin32AppIds", data.Keys);
            Assert.DoesNotContain("espTrackedUserWin32AppIds", data.Keys);
            Assert.DoesNotContain("espTrackedMsiProductCodes", data.Keys);
            Assert.DoesNotContain("espTrackedModernAppPfns", data.Keys);
            Assert.DoesNotContain("espTrackedWin32Count", data.Keys);
            Assert.DoesNotContain("TrackedApps", evt.Payload![SignalPayloadKeys.Message]);
        }
    }
}
