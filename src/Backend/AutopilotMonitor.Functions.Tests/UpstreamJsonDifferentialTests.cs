using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services.GraphResolution;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Differential test for the move of the Graph and Entra token-endpoint parsers to System.Text.Json (D-420):
/// a deterministic generator (fixed seed per parser) builds response bodies, and the production parser must
/// yield exactly what the Newtonsoft reference in <see cref="LegacyUpstreamParsers"/> yields: same result,
/// or both throw (every caller treats any escaped exception the same way).
/// The bodies vary everything that stays identical by contract: root kind, empty and broken bodies, trailing
/// content, comments, trailing commas, whitespace, property order, unknown and duplicate properties, value
/// lists of mixed entries, text with escapes and Unicode, paging links, lifetimes as number or numeric text,
/// error-code variants. They leave out the inputs of the deliberate differences D1 to D6 (JSON null or non-text
/// in a text field, date-shaped text, dates with fractions or offsets, unusable lifetimes, codes beyond Int64);
/// those are pinned in <see cref="GraphResponseParsingTests"/>. A body cut off mid-object (D7) is compared with
/// the production parser's answer to an unreadable body instead: Newtonsoft's DeserializeObject read the part
/// before the cut as if the open containers were closed.
/// Temporary: removed with <see cref="LegacyUpstreamParsers"/> after a clean production run (backlog).
/// </summary>
public class UpstreamJsonDifferentialTests
{
    private const int CaseCount = 20_000;
    private const string Serial = "PF3XKQ7";
    private const string IntuneId = "6f1c4d2a-0b3e-4c5d-8e9f-1a2b3c4d5e6f";

    private static readonly string CorporateId =
        CorporateIdentifierValidator.BuildNormalizedIdentifier("Microsoft Corporation", "Virtual Machine", "1234-5678");

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
            b => ScriptDisplayNameResolver.ReadScriptDisplayName(b) ?? "<null>",
            b => LegacyUpstreamParsers.ReadScriptDisplayName(b) ?? "<null>",
            hit: o => o != "<null>" && o != "<throws>");

    // ── harness ────────────────────────────────────────────────────────────────

    private static void Compare(int seed, Func<Cases, string> body, Func<string, string> actual, Func<string, string> reference, Func<string, bool> hit)
    {
        // The reference formats dates in the current culture before parsing them back; en-US keeps its
        // whole-second "Z" dates exact. The culture swap on other hosts is pinned in GraphResponseParsingTests.
        var saved = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("en-US");
        try
        {
            var generator = new Cases(seed);
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

    private static string Outcome(Func<string> run)
    {
        try { return run(); }
        catch (Exception ex) when (ex is not Xunit.Sdk.XunitException) { return "<throws>"; }
    }

    private static string Text(string? value) => value is null ? "<null>" : "\"" + value + "\"";

    private static string Date(DateTime? value) => value is { } d ? d.ToString("O", CultureInfo.InvariantCulture) + "/" + d.Kind : "<null>";

    // Every consumer reads the token with IsNullOrEmpty, so an empty and an absent token describe alike.
    private static string Describe((string? AccessToken, long? ExpiresInSeconds) r) =>
        $"token={(string.IsNullOrEmpty(r.AccessToken) ? "<none>" : Text(r.AccessToken))} expires={r.ExpiresInSeconds?.ToString(CultureInfo.InvariantCulture) ?? "<null>"}";

    private static string Describe(EntraTokenRejection r) => $"status={r.StatusCode} code={Text(r.ErrorCode)} description={Text(r.Description)}";

    private static string Describe(AutopilotMonitor.Shared.Models.AutopilotRegistration? r) => r is null
        ? "<not registered>"
        : $"id={Text(r.DeviceId)} tag={Text(r.GroupTag)} po={Text(r.PurchaseOrderId)} name={Text(r.DisplayName)} user={r.UserAssigned}";

    private static string Describe((bool Found, string NextUrl) r) => $"found={r.Found} next={Text(r.NextUrl)}";

    private static string Describe(DeviceAssociationResult r) =>
        $"valid={r.IsValid} transient={r.IsTransient} serial={Text(r.SerialNumber)} state={Text(r.AssociationState)} policy={Text(r.DevicePreparationPolicyId)} " +
        $"preBy={Text(r.PreAssociatedByUserPrincipalName)} assigned={Text(r.AssignedToUserPrincipalName)} pre={Date(r.PreAssociationDateTime)} " +
        $"at={Date(r.AssociationDateTime)} device={Text(r.ManagedDeviceId)} error={Text(r.ErrorMessage)}";

    private static string Describe(CloudPcValidationResult r) =>
        $"valid={r.IsValid} transient={r.IsTransient} device={Text(r.IntuneDeviceId)} cpc={Text(r.CloudPcId)} name={Text(r.ManagedDeviceName)} " +
        $"plan={Text(r.ServicePlanName)} error={Text(r.ErrorMessage)}";

    private static string Describe(IntuneDeviceBindingResult r) =>
        $"outcome={r.Outcome} device={Text(r.IntuneDeviceId)} name={Text(r.DeviceName)} aad={Text(r.AzureAdDeviceId)} state={Text(r.ManagementState)} " +
        $"serial={Text(r.SerialNumber)} owner={Text(r.OwnerType)} enrollment={Text(r.EnrollmentType)} registered={r.AzureAdRegistered?.ToString() ?? "<null>"} " +
        $"enrolled={(r.EnrolledDateTime is { } e ? e.ToString("O", CultureInfo.InvariantCulture) : "<null>")} error={Text(r.ErrorMessage)}";

    // A null or empty next link both end the full pull, so they describe alike.
    private static string Describe((List<ScriptDisplayNameResolver.ScriptListItem> Scripts, string? NextUrl) r) =>
        string.Join(";", r.Scripts.Select(s => $"{Text(s.Id)}={Text(s.DisplayName)}/{Text(s.FileName)}")) + $" next={Text(string.IsNullOrEmpty(r.NextUrl) ? null : r.NextUrl)}";

    /// <summary>Response bodies without any input of the deliberate differences D1 to D6.</summary>
    internal sealed class Cases
    {
        private readonly Random _rng;

        public Cases(int seed) => _rng = new Random(seed);

        private T Pick<T>(params T[] items) => items[_rng.Next(items.Length)];

        private bool Chance(int percent) => _rng.Next(100) < percent;

        private static string Str(string s) => System.Text.Json.JsonSerializer.Serialize(s);

        private string Ws() => Pick("", "", "", " ", "\n", "\r\n  ", "\t");

        /// <summary>Plain text that is never a date in either library and never JSON null.</summary>
        private string Text() => Pick(
            Str("value"), Str("  padded  "), Str("UPPER lower 123"), "\"Ünïcødé ☃ ε\"", Str("quote\" back\\ slash\nnew line"),
            Str(new string('x', 300)), Str(""), Str("   "), Str("a/b?c=d&e"), Str("é\u0001\u007f"), Str("2026"), Str("12:16"));

        private string Url() => Pick(
            Str("https://graph.microsoft.com/beta/next?$skiptoken=abc"), Str("https://graph.microsoft.com/v1.0/x?$top=5"));

        /// <summary>A whole-second UTC date, a date without time, or a value both read as no date.</summary>
        private string DateValue() => Pick(
            Str("2026-05-08T12:16:49Z"), Str("2025-12-31T23:59:59Z"), Str("2026-05-08"), Str("0001-01-01T00:00:00Z"),
            Str(""), Str("not-a-date"), "null");

        /// <summary>A property: (name, json) or (name, null) when the property is left out.</summary>
        private (string Name, string? Json) Prop(string name, string json, int missingPercent = 15)
            => (name, Chance(missingPercent) ? null : json);

        private string Obj(params (string Name, string? Json)[] props)
        {
            var parts = props.Where(p => p.Json != null).Select(p => $"{Str(p.Name)}:{Ws()}{p.Json}").ToList();
            if (Chance(10))
                parts.Insert(_rng.Next(parts.Count + 1), $"{Str("unknownProperty")}:{Pick("1", "\"x\"", "{}", "[]", "null", "true")}");
            if (Chance(5) && parts.Count > 0)
            {
                // Duplicate key with the same value: whichever occurrence wins, the meaning stays.
                parts.Insert(0, parts[_rng.Next(parts.Count)]);
            }
            if (Chance(30))
                parts = parts.OrderBy(_ => _rng.Next()).ToList();
            var separator = Pick(",", ",", ", ", ",\n  ", ", /*c*/ ", ",// line\n");
            var trailing = parts.Count > 0 && Chance(5) ? "," : "";
            return "{" + Ws() + string.Join(separator, parts) + trailing + Ws() + "}";
        }

        private string ValueList(Func<string> item)
        {
            switch (_rng.Next(100))
            {
                case < 5: return "null";
                case < 9: return Pick("\"oops\"", "{}", "42");
                case < 14: return "[]";
            }
            var entries = new List<string>();
            var count = _rng.Next(1, 6);
            for (var i = 0; i < count; i++)
                entries.Add(_rng.Next(100) switch { < 85 => item(), < 90 => "null", < 94 => Pick("1", "\"x\"", "true"), < 97 => "[]", _ => "{}" });
            return "[" + Ws() + string.Join("," + Ws(), entries) + Ws() + "]";
        }

        /// <summary>Whether the last body was the object cut off halfway (D7).</summary>
        public bool LastWasCutOff { get; private set; }

        /// <summary>The body around an object: mostly the object itself, else a root or shape both refuse.</summary>
        private string Root(string objectJson)
        {
            var pick = _rng.Next(100);
            LastWasCutOff = pick is >= 92 and < 95;
            return pick switch
            {
                < 82 => Ws() + objectJson + Ws(),
                < 84 => "",
                < 86 => "   ",
                < 88 => "null",
                < 90 => "[" + objectJson + "]",
                < 92 => Pick("42", "\"text\"", "true"),
                < 95 => objectJson.Substring(0, Math.Max(1, objectJson.Length / 2)),
                < 98 => objectJson + Pick(" x", " {}", " 1"),
                _ => Pick("<html>busy</html>", "not json"),
            };
        }

        private string Matching(string target) => Pick(
            Str(target), Str(target.ToLowerInvariant()), Str("  " + target + " "), Str(target + "X"), Str("OTHER"), Str(""), "12345", "true");

        public string TokenBody() => Root(Obj(
            Prop("access_token", Pick(Str("eyJ0eXAi.token"), Str("t"), Str(""), "null")),
            Prop("expires_in", Pick("3599", "0", "-5", "7200", "9223372036854775807", "\"3599\"", "\" 3599 \"", "\"+3599\"", "\"-1\"", "null")),
            Prop("token_type", Str("Bearer"))));

        public string EntraBody() => Root(Obj(
            Prop("error", Pick(Str("invalid_client"), Str("unauthorized_client"))),
            Prop("error_description", Pick(Str("AADSTS7000229: The client application is missing a service principal.\r\nTrace ID: x"),
                Str("AADSTS53003: Access has been blocked by Conditional Access policies."), Str("no code here"), Str("AADSTS: no digits"),
                Str(new string('d', 400)), Str("   "), "null", "42", "true", "{}", "[]")),
            Prop("error_codes", Pick("[7000229]", "[53003,7000229]", "[\"7000229\",53003]", "[7000229.5]", "[7e6]", "[-1]", "[]", "7000229", "null",
                "[" + _rng.Next(1, 99_999_999) + "]")),
            Prop("timestamp", Str("2026-11-05 08:00:00Z"))));

        public string AutopilotBody(string serial) => Root(Obj(
            Prop("@odata.context", Str("https://graph.microsoft.com/v1.0/$metadata#deviceManagement/windowsAutopilotDeviceIdentities")),
            Prop("value", ValueList(() => Obj(
                Prop("id", Pick(Text(), Str("ap-1"), "null", "42")),
                Prop("serialNumber", Matching(serial), 10),
                Prop("groupTag", Pick(Text(), "null", "true")),
                Prop("purchaseOrderIdentifier", Pick(Text(), "null", "{}")),
                Prop("displayName", Pick(Text(), "null", "[]")),
                Prop("userPrincipalName", Pick(Text(), "null", Str("u@contoso.com"))))), 5)));

        public string CorporateBody()
        {
            var identifier = Pick(Str("Microsoft Corporation,Virtual Machine,1234-5678"), Str("MICROSOFTCORPORATION,VIRTUALMACHINE,12345678"),
                Str("microsoft corporation, virtual machine, 1234 5678"), Str("Other,Model,1"), Str(""), "null", "42");
            return Root(Obj(
                Prop("value", ValueList(() => Obj(
                    Prop("importedDeviceIdentityType", Pick(Str("manufacturerModelSerial"), Str("MANUFACTURERMODELSERIAL"), Str("serialNumber"), "null")),
                    Prop("importedDeviceIdentifier", identifier))), 5),
                Prop("@odata.nextLink", Pick(Url(), Str(""), "null"), 50)));
        }

        public string AssociationBody(string serial) => Root(Obj(
            Prop("value", ValueList(() => Obj(
                Prop("serialNumber", Matching(serial), 10),
                Prop("associationState", Pick(Str("associated"), Str("preAssociated"), Text())),
                Prop("devicePreparationPolicyId", Text()),
                Prop("preassociatedByUserPrincipalName", Text()),
                Prop("assignedToUserPrincipalName", Text()),
                Prop("preassociationDateTime", DateValue()),
                Prop("associationDateTime", DateValue()),
                Prop("managedDeviceId", Text()))), 5)));

        public string CloudPcBody(string intuneId) => Root(Obj(
            Prop("value", ValueList(() => Obj(
                Prop("id", Text()),
                Prop("managedDeviceId", Matching(intuneId), 10),
                Prop("managedDeviceName", Text()),
                Prop("servicePlanName", Text()))), 5)));

        public string ManagedDeviceBody(string intuneId) => Root(Obj(
            Prop("id", Matching(intuneId), 10),
            Prop("deviceName", Text()),
            Prop("azureADDeviceId", Text()),
            Prop("managementState", Pick(Str("managed"), Str("retirePending"), Str("WIPEISSUED"), Str("deletePending"), Text())),
            Prop("serialNumber", Pick(Text(), "null")),
            Prop("managedDeviceOwnerType", Pick(Str("company"), Text(), "null")),
            Prop("deviceEnrollmentType", Pick(Str("windowsAzureADJoin"), Text(), "null")),
            Prop("azureADRegistered", Pick("true", "false", "null", "\"true\"", "1")),
            Prop("enrolledDateTime", DateValue())));

        public string ScriptPageBody() => Root(Obj(
            Prop("value", ValueList(() => Obj(
                Prop("id", Pick(Str("s1"), Str("4f9e2c1a-0000-0000-0000-000000000001"), Str(""), "null"), 10),
                Prop("displayName", Text()),
                Prop("fileName", Text()))), 5),
            Prop("@odata.nextLink", Pick(Url(), Str(""), "null"), 50)));

        public string ScriptNameBody() => Root(Obj(
            Prop("id", Str("s1")),
            Prop("displayName", Text()),
            Prop("fileName", Text())));
    }
}
