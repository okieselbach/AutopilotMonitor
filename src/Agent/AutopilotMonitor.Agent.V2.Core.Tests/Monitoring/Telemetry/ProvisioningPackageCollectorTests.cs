#nullable enable
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Provisioning;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Telemetry;

/// <summary>
/// Unit tests for the pure aggregation in <see cref="ProvisioningPackageCollector"/>
/// (<c>BuildPayload</c> / <c>BuildContentIndicators</c>). The IO probes are environment
/// dependent and not exercised here; this locks the fact-shaping + classification rules.
/// </summary>
public sealed class ProvisioningPackageCollectorTests
{
    [Fact]
    public void BuildPayload_clean_device_reports_nothing_found()
    {
        var findings = new ProvisioningScanFindings();

        var payload = ProvisioningPackageCollector.BuildPayload(findings);

        Assert.False((bool)payload["anyPpkgFound"]);
        Assert.Equal(0, (int)payload["ppkgFileCount"]);
        Assert.Equal(0, (int)payload["packageCount"]);
        Assert.False((bool)payload["recoveryCustomizationsResidue"]);
        Assert.False((bool)payload["omadmAccountsPresent"]);
        Assert.Empty((List<string>)payload["scanErrors"]);

        var indicators = (Dictionary<string, object>)payload["contentIndicators"];
        Assert.False((bool)indicators["localAccounts"]);
        Assert.False((bool)indicators["certificates"]);
        Assert.False((bool)indicators["wifiProfiles"]);
        Assert.False((bool)indicators["vpnProfiles"]);
        Assert.False((bool)indicators["appManagement"]);
        Assert.False((bool)indicators["scripts"]);
        Assert.True(indicators.ContainsKey("note"));
    }

    [Fact]
    public void BuildPayload_embeds_artifacts_array_consistent_with_anyPpkgFound()
    {
        // One aggregate event carries all PPKGs as artifacts[] (no per-package events) — the rules
        // iterate this array. artifactCount + anyPpkgFound must agree with the array length.
        var findings = new ProvisioningScanFindings();
        findings.Files.Add(new PpkgFileFact
        {
            Directory = @"C:\WINDOWS\Provisioning",
            Name = "Power.Settings.Sleep.ppkg",
            FullPath = @"C:\WINDOWS\Provisioning\Packages\Power.Settings.Sleep.ppkg",
        });

        var payload = ProvisioningPackageCollector.BuildPayload(findings);

        Assert.True((bool)payload["anyPpkgFound"]);
        Assert.Equal(1, (int)payload["artifactCount"]);
        var artifacts = (List<Dictionary<string, object>>)payload["artifacts"];
        var only = Assert.Single(artifacts);
        Assert.Equal("Power.Settings.Sleep.ppkg", only["identity"]);
        Assert.Equal("file", only["source"]);
    }

    [Fact]
    public void BuildPayload_clean_device_has_empty_artifacts()
    {
        var payload = ProvisioningPackageCollector.BuildPayload(new ProvisioningScanFindings());

        Assert.False((bool)payload["anyPpkgFound"]);
        Assert.Equal(0, (int)payload["artifactCount"]);
        Assert.Empty((List<Dictionary<string, object>>)payload["artifacts"]);
    }

    [Fact]
    public void BuildPayload_ppkg_file_present_sets_anyPpkgFound()
    {
        var findings = new ProvisioningScanFindings();
        findings.Files.Add(new PpkgFileFact
        {
            Directory = @"C:\ProgramData\Microsoft\Provisioning",
            Name = "bulk.ppkg",
            FullPath = @"C:\ProgramData\Microsoft\Provisioning\bulk.ppkg",
            SizeBytes = 4096,
            LastWriteUtc = "2026-06-06T10:00:00.0000000Z",
        });

        var payload = ProvisioningPackageCollector.BuildPayload(findings);

        Assert.True((bool)payload["anyPpkgFound"]);
        Assert.Equal(1, (int)payload["ppkgFileCount"]);
        // Detail lives ONLY in the unified artifacts array (no duplicate ppkgFiles array).
        Assert.False(payload.ContainsKey("ppkgFiles"));
        var artifacts = (List<Dictionary<string, object>>)payload["artifacts"];
        var file = Assert.Single(artifacts);
        Assert.Equal("file", file["source"]);
        Assert.Equal("bulk.ppkg", file["identity"]);
        Assert.Equal(4096L, file["sizeBytes"]);
    }

    [Fact]
    public void BuildPayload_package_metadata_projected_into_artifacts_subkeys_not_on_wire()
    {
        var findings = new ProvisioningScanFindings();
        var pkg = new PpkgPackageFact
        {
            PackageId = "{abc}",
            Name = "Contoso Bulk",
            OwnerType = "ITPro",
            Rank = "100",
            InstallTime = "2026-06-06T09:00:00Z",
        };
        pkg.SubKeyNames.Add("WiFi");
        pkg.SubKeyNames.Add("Accounts");
        findings.Packages.Add(pkg);

        var payload = ProvisioningPackageCollector.BuildPayload(findings);

        Assert.True((bool)payload["anyPpkgFound"]);
        Assert.Equal(1, (int)payload["packageCount"]);
        Assert.False(payload.ContainsKey("packages"));
        var artifacts = (List<Dictionary<string, object>>)payload["artifacts"];
        var first = Assert.Single(artifacts);
        Assert.Equal("registry", first["source"]);
        // identity carries name | ownerType | packageId (no redundant scalar copies on the wire).
        Assert.Equal("Contoso Bulk | ITPro | {abc}", first["identity"]);
        Assert.False(first.ContainsKey("packageId"));
        Assert.False(first.ContainsKey("packageName"));
        // Raw registry subkeys are NOT serialized (redundant with contentIndicators, size bloat).
        Assert.False(first.ContainsKey("registrySubKeys"));

        // Content indicators are still derived from the package-scoped subkey names.
        var indicators = (Dictionary<string, object>)payload["contentIndicators"];
        Assert.True((bool)indicators["wifiProfiles"]);
        Assert.True((bool)indicators["localAccounts"]);
        Assert.False((bool)indicators["vpnProfiles"]);
    }

    [Fact]
    public void BuildPayload_omadm_alone_is_context_not_a_ppkg_signal()
    {
        // OMADM\Accounts exists on every MDM-enrolled device — must NOT flip anyPpkgFound.
        var findings = new ProvisioningScanFindings { OmadmAccountsPresent = true, DiagnosticsPresent = true };

        var payload = ProvisioningPackageCollector.BuildPayload(findings);

        Assert.False((bool)payload["anyPpkgFound"]);
        Assert.True((bool)payload["omadmAccountsPresent"]);
        Assert.True((bool)payload["provisioningDiagnosticsPresent"]);
    }

    [Fact]
    public void BuildPayload_recovery_residue_alone_sets_anyPpkgFound_and_emits_detected_event()
    {
        // Non-.ppkg residue in Recovery\Customizations is the gap case: it must still flip
        // anyPpkgFound AND produce a detected event so ANALYZE-SEC-005 fires on residue-only devices.
        var findings = new ProvisioningScanFindings { RecoveryCustomizationsDir = @"C:\Recovery\Customizations" };
        findings.RecoveryCustomizationsFiles.Add("setupcomplete.cmd");

        var payload = ProvisioningPackageCollector.BuildPayload(findings);

        Assert.True((bool)payload["anyPpkgFound"]);
        Assert.True((bool)payload["recoveryCustomizationsResidue"]);
        var artifacts = (List<Dictionary<string, object>>)payload["artifacts"];
        var residue = Assert.Single(artifacts);
        Assert.Equal("recovery_residue", residue["source"]);
        Assert.Equal("setupcomplete.cmd", residue["identity"]);
    }

    [Fact]
    public void BuildDetectedEvents_skips_ppkg_residue_to_avoid_duplicate_with_file_event()
    {
        // A .ppkg in Recovery is captured BOTH as a .ppkg file AND as a residue name; it must
        // produce exactly one detected event (the file event), not two.
        var findings = new ProvisioningScanFindings { RecoveryCustomizationsDir = @"C:\Recovery\Customizations" };
        findings.Files.Add(new PpkgFileFact
        {
            Directory = @"C:\Recovery\Customizations",
            Name = "oem.ppkg",
            FullPath = @"C:\Recovery\Customizations\oem.ppkg",
        });
        findings.RecoveryCustomizationsFiles.Add("oem.ppkg");

        var detected = ProvisioningPackageCollector.BuildDetectedEvents(findings);

        var only = Assert.Single(detected);
        Assert.Equal("file", only["source"]);
    }

    [Fact]
    public void BuildDetectedEvents_emits_ppkg_residue_when_file_enumeration_missed_it()
    {
        // Gap case: recursive *.ppkg enumeration failed/truncated, so the .ppkg is NOT in
        // findings.Files. The residue pass must still emit a detected event (dedup is by captured
        // path, not by ".ppkg" extension), so anyPpkgFound stays consistent with a firing rule.
        var findings = new ProvisioningScanFindings { RecoveryCustomizationsDir = @"C:\Recovery\Customizations" };
        findings.RecoveryCustomizationsFiles.Add("uncaptured.ppkg");

        var detected = ProvisioningPackageCollector.BuildDetectedEvents(findings);

        var only = Assert.Single(detected);
        Assert.Equal("recovery_residue", only["source"]);
        Assert.Equal("uncaptured.ppkg", only["identity"]);
        Assert.True((bool)ProvisioningPackageCollector.BuildPayload(findings)["anyPpkgFound"]);
    }

    [Fact]
    public void AnyPpkgFound_is_consistent_with_detected_event_count()
    {
        // The invariant the Recovery-residue fix guarantees: anyPpkgFound <=> a detected event exists.
        var clean = new ProvisioningScanFindings { OmadmAccountsPresent = true, DiagnosticsPresent = true };
        Assert.False((bool)ProvisioningPackageCollector.BuildPayload(clean)["anyPpkgFound"]);
        Assert.Empty(ProvisioningPackageCollector.BuildDetectedEvents(clean));

        var residueOnly = new ProvisioningScanFindings { RecoveryCustomizationsDir = @"C:\Recovery\Customizations" };
        residueOnly.RecoveryCustomizationsFiles.Add("unattend.xml");
        Assert.True((bool)ProvisioningPackageCollector.BuildPayload(residueOnly)["anyPpkgFound"]);
        Assert.NotEmpty(ProvisioningPackageCollector.BuildDetectedEvents(residueOnly));
    }

    [Fact]
    public void BuildRecoveryResidueEventData_projects_scalar_fields_with_identity()
    {
        var data = ProvisioningPackageCollector.BuildRecoveryResidueEventData("unattend.xml", @"C:\Recovery\Customizations");

        Assert.Equal("recovery_residue", data["source"]);
        Assert.Equal("unattend.xml", data["identity"]);
        Assert.Equal(@"C:\Recovery\Customizations", data["dir"]);
        Assert.False(data.ContainsKey("fileName"));
    }

    [Fact]
    public void BuildPayload_artifacts_are_complete_no_count_cap()
    {
        // The rule-facing artifacts list must be COMPLETE — a count cap could chop a malicious
        // artifact off the tail so the allow-list rule never sees it. Entries are lean instead.
        var findings = new ProvisioningScanFindings { RecoveryCustomizationsDir = @"C:\Recovery\Customizations" };
        for (int i = 0; i < 130; i++)
            findings.RecoveryCustomizationsFiles.Add($"residue_{i}.txt");

        var payload = ProvisioningPackageCollector.BuildPayload(findings);

        Assert.Equal(130, (int)payload["artifactCount"]);
        Assert.Equal(130, ((List<Dictionary<string, object>>)payload["artifacts"]).Count);
        Assert.False(payload.ContainsKey("artifactsTruncated"));
    }

    [Fact]
    public void BuildChunkedPayloads_single_event_when_small()
    {
        var findings = new ProvisioningScanFindings { RecoveryCustomizationsDir = @"C:\Recovery\Customizations" };
        for (int i = 0; i < 10; i++) findings.RecoveryCustomizationsFiles.Add($"r_{i}.txt");
        var payload = ProvisioningPackageCollector.BuildPayload(findings);
        var artifacts = (List<Dictionary<string, object>>)payload["artifacts"];

        var chunks = ProvisioningPackageCollector.BuildChunkedPayloads(payload, artifacts);

        var only = Assert.Single(chunks);
        Assert.Equal(0, (int)only["chunkIndex"]);
        Assert.Equal(1, (int)only["chunkCount"]);
        Assert.Equal(10, ((List<Dictionary<string, object>>)only["artifacts"]).Count);
        Assert.True(only.ContainsKey("contentIndicators")); // full aggregate on chunk 0
    }

    [Fact]
    public void BuildChunkedPayloads_splits_large_set_across_events_without_loss()
    {
        // Many artifacts → multiple size-bounded events. The rule engine evaluates the array
        // condition across all of them, so splitting (vs dropping) preserves full coverage.
        var findings = new ProvisioningScanFindings { RecoveryCustomizationsDir = @"C:\Recovery\Customizations" };
        for (int i = 0; i < 1000; i++) findings.RecoveryCustomizationsFiles.Add($"r_{i:D4}.txt");
        var payload = ProvisioningPackageCollector.BuildPayload(findings);
        var artifacts = (List<Dictionary<string, object>>)payload["artifacts"];
        Assert.Equal(1000, artifacts.Count);

        var chunks = ProvisioningPackageCollector.BuildChunkedPayloads(payload, artifacts);

        Assert.True(chunks.Count >= 2, "large set should span multiple events");
        // Chunk 0 carries the full aggregate; continuation chunks are lean.
        Assert.True(chunks[0].ContainsKey("contentIndicators"));
        Assert.False(chunks[1].ContainsKey("contentIndicators"));

        for (int i = 0; i < chunks.Count; i++)
        {
            Assert.Equal(i, (int)chunks[i]["chunkIndex"]);
            Assert.Equal(chunks.Count, (int)chunks[i]["chunkCount"]);
            Assert.Equal(1000, (int)chunks[i]["artifactCount"]);
            AssertChunkUnderStorageLimit(chunks[i]);
        }

        // No artifact lost or duplicated across chunks.
        var ids = chunks
            .SelectMany(c => (List<Dictionary<string, object>>)c["artifacts"])
            .Select(a => (string)a["identity"])
            .ToList();
        Assert.Equal(1000, ids.Count);
        Assert.Equal(1000, ids.Distinct().Count());
    }

    [Fact]
    public void BuildChunkedPayloads_worstcase_long_identities_stay_under_storage_limit()
    {
        // Reviewer scenario: 150 artifacts with ~220-char names would blow a fixed-count chunk past
        // 30k. Size-based chunking must keep EVERY chunk's serialized DataJson under the limit.
        var longName = new string('a', 220) + ".ppkg";
        var findings = new ProvisioningScanFindings { RecoveryCustomizationsDir = @"C:\ProgramData\Microsoft\Provisioning\VeryDeep\Nested\Path" };
        for (int i = 0; i < 150; i++) findings.RecoveryCustomizationsFiles.Add($"{i:D3}_{longName}");
        var payload = ProvisioningPackageCollector.BuildPayload(findings);
        var artifacts = (List<Dictionary<string, object>>)payload["artifacts"];

        var chunks = ProvisioningPackageCollector.BuildChunkedPayloads(payload, artifacts);

        Assert.True(chunks.Count >= 2);
        foreach (var chunk in chunks)
            AssertChunkUnderStorageLimit(chunk);

        // Still no loss despite the worst-case sizes.
        var ids = chunks.SelectMany(c => (List<Dictionary<string, object>>)c["artifacts"]).Select(a => (string)a["identity"]).ToList();
        Assert.Equal(150, ids.Count);
        Assert.Equal(150, ids.Distinct().Count());
    }

    // Mirrors the backend's 30k DataJson truncation guard using the SAME serializer (Newtonsoft).
    private static void AssertChunkUnderStorageLimit(Dictionary<string, object> chunk)
    {
        var json = Newtonsoft.Json.JsonConvert.SerializeObject(chunk, Newtonsoft.Json.Formatting.None);
        Assert.True(json.Length < 30000, $"chunk DataJson {json.Length} chars exceeds 30k truncation limit");
    }

    [Fact]
    public void BuildPackageEventData_caps_rank_and_installTime()
    {
        var data = ProvisioningPackageCollector.BuildPackageEventData(new PpkgPackageFact
        {
            PackageId = "{x}",
            Name = "P",
            Rank = new string('9', 40000),
            InstallTime = new string('t', 40000),
        });

        Assert.True(((string)data["rank"]).Length <= 256);
        Assert.True(((string)data["installTime"]).Length <= 256);
    }

    [Fact]
    public void BuildChunkedPayloads_oversized_registry_field_stays_under_storage_limit()
    {
        // A single registry package with a pathological 40k rank must NOT push its (un-splittable)
        // chunk past 30k — every per-artifact string is capped.
        var findings = new ProvisioningScanFindings();
        findings.Packages.Add(new PpkgPackageFact { PackageId = "{x}", Name = "Evil", Rank = new string('9', 40000) });
        var payload = ProvisioningPackageCollector.BuildPayload(findings);
        var artifacts = (List<Dictionary<string, object>>)payload["artifacts"];

        var chunks = ProvisioningPackageCollector.BuildChunkedPayloads(payload, artifacts);

        foreach (var chunk in chunks)
            AssertChunkUnderStorageLimit(chunk);
    }

    [Fact]
    public void BuildIdentity_hard_caps_length()
    {
        var identity = ProvisioningPackageCollector.BuildIdentity(
            name: new string('x', 1000), fileName: null, ownerType: null, packageId: null);

        Assert.Equal(256, identity.Length);
    }

    [Fact]
    public void BuildPayload_scanTruncated_reflects_per_source_caps()
    {
        // When a per-source collection cap bit, scanTruncated must be true so ANALYZE-SEC-007 alarms.
        var clean = new ProvisioningScanFindings();
        Assert.False((bool)ProvisioningPackageCollector.BuildPayload(clean)["scanTruncated"]);

        var floodedFiles = new ProvisioningScanFindings { FilesTruncated = true };
        Assert.True((bool)ProvisioningPackageCollector.BuildPayload(floodedFiles)["scanTruncated"]);

        var floodedResidue = new ProvisioningScanFindings { RecoveryFilesTruncated = true };
        Assert.True((bool)ProvisioningPackageCollector.BuildPayload(floodedResidue)["scanTruncated"]);
    }

    [Fact]
    public void BuildPayload_surfaces_scan_errors_fail_soft()
    {
        var findings = new ProvisioningScanFindings();
        findings.Errors.Add("registry:UnauthorizedAccessException: denied");

        var payload = ProvisioningPackageCollector.BuildPayload(findings);

        var errors = (List<string>)payload["scanErrors"];
        Assert.Single(errors);
        Assert.Contains("denied", errors[0]);
    }

    [Fact]
    public void BuildIdentity_joins_non_empty_distinct_fields()
    {
        var identity = ProvisioningPackageCollector.BuildIdentity(
            name: "Dell Recovery", fileName: "dell_recovery.ppkg", ownerType: "OEM", packageId: "{guid}");

        Assert.Equal("Dell Recovery | dell_recovery.ppkg | OEM | {guid}", identity);
    }

    [Fact]
    public void BuildIdentity_skips_empty_and_dedups_case_insensitive()
    {
        var identity = ProvisioningPackageCollector.BuildIdentity(
            name: "bulk", fileName: "bulk", ownerType: null, packageId: "  ");

        Assert.Equal("bulk", identity);
    }

    [Fact]
    public void BuildPackageEventData_projects_scalar_fields_with_identity()
    {
        var data = ProvisioningPackageCollector.BuildPackageEventData(new PpkgPackageFact
        {
            PackageId = "{abc}",
            Name = "Contoso Bulk",
            OwnerType = "ITPro",
        });

        Assert.Equal("registry", data["source"]);
        Assert.Equal("Contoso Bulk | ITPro | {abc}", data["identity"]);
        Assert.False(data.ContainsKey("packageId"));
        Assert.False(data.ContainsKey("packageName"));
    }

    [Fact]
    public void BuildFileEventData_projects_scalar_fields_with_identity()
    {
        var data = ProvisioningPackageCollector.BuildFileEventData(new PpkgFileFact
        {
            Directory = @"C:\Recovery\Customizations",
            Name = "oem.ppkg",
            FullPath = @"C:\Recovery\Customizations\oem.ppkg",
            SizeBytes = 123,
        });

        Assert.Equal("file", data["source"]);
        Assert.Equal("oem.ppkg", data["identity"]);
        Assert.Equal(@"C:\Recovery\Customizations", data["dir"]);
        Assert.Equal(123L, data["sizeBytes"]);
        Assert.False(data.ContainsKey("fileName"));
    }

    [Theory]
    [InlineData("EnterpriseDesktopAppManagement", "appManagement")]
    [InlineData("RootCATrustedCertificates", "certificates")]
    [InlineData("ClientCertificateInstall", "certificates")]
    [InlineData("VPNv2", "vpnProfiles")]
    [InlineData("WLAN", "wifiProfiles")]
    [InlineData("ProvisioningCommands", "scripts")]
    public void BuildContentIndicators_maps_known_csp_markers(string subKey, string expectedIndicator)
    {
        var findings = new ProvisioningScanFindings();
        var pkg = new PpkgPackageFact { PackageId = "{x}" };
        pkg.SubKeyNames.Add(subKey);
        findings.Packages.Add(pkg);

        var indicators = ProvisioningPackageCollector.BuildContentIndicators(findings);

        Assert.True((bool)indicators[expectedIndicator]);
    }
}
