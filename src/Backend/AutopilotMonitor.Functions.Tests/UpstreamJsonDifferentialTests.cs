using System;
using System.Collections.Generic;
using System.Globalization;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services.GraphResolution;
using Xunit;
using static AutopilotMonitor.Functions.Tests.UpstreamParserReadings;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Differential test for the move of the Graph and Entra token-endpoint parsers to System.Text.Json (D-420):
/// <see cref="UpstreamResponseCases"/> builds response bodies from a fixed seed per parser, and the production
/// parser must yield exactly what the Newtonsoft reference in <see cref="LegacyUpstreamParsers"/> yields: same
/// result, or both throw (every caller treats any escaped exception the same way).
/// The bodies leave out the inputs of the deliberate differences D1 to D6; those are pinned in
/// <see cref="GraphResponseParsingTests"/>. A body cut off mid-object (D7) is compared with the production
/// parser's answer to an unreadable body instead: Newtonsoft's DeserializeObject read the part before the cut
/// as if the open containers were closed.
/// Temporary: removed with <see cref="LegacyUpstreamParsers"/> after a clean production run (backlog). The
/// generator and the readings stay for <see cref="UpstreamJsonInvariantTests"/>.
/// </summary>
public class UpstreamJsonDifferentialTests
{
    private const int CaseCount = 20_000;

    [Fact]
    public void Token_response_matches_the_reference()
        => Compare(seed: 4201, g => g.TokenBody(),
            b => Describe(GraphTokenService.ReadTokenResponse(b)),
            b => Describe(LegacyUpstreamParsers.ReadTokenResponse(b)),
            hit: o => o.StartsWith("token=\"", StringComparison.Ordinal));

    [Fact]
    public void Entra_rejection_matches_the_reference()
        => Compare(seed: 4202, g => g.EntraBody(),
            b => Describe(EntraTokenRejection.Parse(400, b)),
            b => Describe(LegacyUpstreamParsers.ParseEntraRejection(400, b)),
            hit: o => o.Contains("code=\"AADSTS", StringComparison.Ordinal));

    [Fact]
    public void Autopilot_list_matches_the_reference()
        => Compare(seed: 4203, g => g.AutopilotBody(Serial),
            b => Describe(AutopilotDeviceValidator.FindRegistration(b, Serial)),
            b => Describe(LegacyUpstreamParsers.FindRegistration(b, Serial)),
            hit: o => o.StartsWith("id=", StringComparison.Ordinal));

    [Fact]
    public void Corporate_page_matches_the_reference()
        => Compare(seed: 4204, g => g.CorporateBody(),
            b => Describe(CorporateIdentifierValidator.ReadIdentityPage(b, CorporateId)),
            b => Describe(LegacyUpstreamParsers.ReadIdentityPage(b, CorporateId)),
            hit: o => o.StartsWith("found=True", StringComparison.Ordinal));

    [Fact]
    public void Association_list_matches_the_reference()
        => Compare(seed: 4205, g => g.AssociationBody(Serial),
            b => Describe(DeviceAssociationValidator.ParseTenantAssociatedDevicesResponse(b, Serial)),
            b => Describe(LegacyUpstreamParsers.ParseAssociation(b, Serial)),
            hit: o => o.StartsWith("valid=True", StringComparison.Ordinal));

    [Fact]
    public void CloudPc_list_matches_the_reference()
        => Compare(seed: 4206, g => g.CloudPcBody(IntuneId),
            b => Describe(CloudPcDeviceValidator.ParseCloudPcResponse(b, IntuneId)),
            b => Describe(LegacyUpstreamParsers.ParseCloudPc(b, IntuneId)),
            hit: o => o.StartsWith("valid=True", StringComparison.Ordinal));

    [Fact]
    public void Managed_device_matches_the_reference()
        => Compare(seed: 4207, g => g.ManagedDeviceBody(IntuneId),
            b => Describe(IntuneDeviceBindingValidator.ParseManagedDeviceResponse(b, IntuneId)),
            b => Describe(LegacyUpstreamParsers.ParseManagedDevice(b, IntuneId)),
            hit: o => !o.StartsWith("outcome=NotFound", StringComparison.Ordinal) && o != "<throws>");

    [Fact]
    public void Script_page_matches_the_reference()
        => Compare(seed: 4208, g => g.ScriptPageBody(),
            b => Describe(ScriptDisplayNameResolver.ReadScriptPage(b, withFileName: true)),
            b => Describe(LegacyUpstreamParsers.ReadScriptPage(b, withFileName: true)),
            hit: o => o.StartsWith("\"", StringComparison.Ordinal));

    [Fact]
    public void Script_name_matches_the_reference()
        => Compare(seed: 4209, g => g.ScriptNameBody(),
            b => Describe(ScriptDisplayNameResolver.ReadScriptDisplayName(b)),
            b => Describe(LegacyUpstreamParsers.ReadScriptDisplayName(b)),
            hit: o => o != "<null>" && o != "<throws>");

    private static void Compare(int seed, Func<UpstreamResponseCases, string> body, Func<string, string> actual, Func<string, string> reference, Func<string, bool> hit)
    {
        // The reference formats dates in the current culture before parsing them back; en-US keeps its
        // whole-second "Z" dates exact. The culture swap on other hosts is pinned in GraphResponseParsingTests.
        var saved = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("en-US");
        try
        {
            var generator = new UpstreamResponseCases(seed);
            var unreadable = Outcome(() => actual("not json"));
            var mismatches = new List<string>();
            int hits = 0, throws = 0;
            for (var i = 0; i < CaseCount && mismatches.Count < 5; i++)
            {
                var input = body(generator);
                var got = Outcome(() => actual(input));
                var want = generator.LastWasCutOff ? unreadable : Outcome(() => reference(input));
                if (got != want)
                    mismatches.Add($"seed {seed}, case {i}: body {input}\n      production: {got}\n      reference:  {want}");
                if (hit(want)) hits++;
                if (want == "<throws>") throws++;
            }
            Assert.True(mismatches.Count == 0, "Parser differs from the Newtonsoft reference:\n  " + string.Join("\n  ", mismatches));

            // The generator must exercise the parser, not compare 20,000 identical misses.
            Assert.True(hits >= CaseCount / 50, $"only {hits} of {CaseCount} cases reach a result worth comparing");
            Assert.True(throws <= CaseCount * 7 / 10, $"{throws} of {CaseCount} cases throw; the generator barely reaches the parser");
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }
}
