using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Deterministic Graph and Entra token-endpoint response bodies for the parser tests of D-420. They vary root
/// kind, empty, broken and cut-off bodies, trailing content, comments, trailing commas, whitespace, property
/// order, unknown and duplicate properties, mixed list entries, text with escapes and Unicode, paging links,
/// lifetimes as number or numeric text, and error-code variants.
/// By default they leave out the inputs of the deliberate differences D1 to D6, so the old and the new parser
/// must agree on them. With <c>everyValueKind</c> they add those inputs too: JSON null, numbers, booleans,
/// objects and arrays in text fields, date-shaped text, dates with fractions and offsets, escapes that are no
/// valid UTF-16, unusable lifetimes and error codes.
/// </summary>
internal sealed class UpstreamResponseCases
{
    // The same few literals come back thousands of times; serializing each once keeps the fuzz fast. Declared
    // first: the pools below call Str while the type initializes.
    private static readonly ConcurrentDictionary<string, string> Serialized = new(StringComparer.Ordinal);

    private readonly Random _rng;
    private readonly bool _everyValueKind;

    public UpstreamResponseCases(int seed, bool everyValueKind = false)
    {
        _rng = new Random(seed);
        _everyValueKind = everyValueKind;
    }

    /// <summary>Whether the last body was the object cut off halfway (D7).</summary>
    public bool LastWasCutOff { get; private set; }

    // Plain text that is never a date in either library and never JSON null.
    private static readonly string[] PlainTexts =
    {
        Str("value"), Str("  padded  "), Str("UPPER lower 123"), "\"Ünïcødé ☃ ε\"", Str("quote\" back\\ slash\nnew line"),
        Str(new string('x', 300)), Str(""), Str("   "), Str("a/b?c=d&e"), Str("é\u0001\u007f"), Str("2026"), Str("12:16"),
    };

    // What D1, D3 and D4 read differently: JSON null, other kinds, date-shaped text, an escape that is no valid UTF-16.
    private static readonly string[] DeliberateTexts =
    {
        "null", "42", "-1.5", "true", "{}", "[]", "[\"x\"]", Str("2026-01-01T00:00:00Z"), Str("/Date(1713096021000)/"), "\"x\\ud800y\"",
    };

    // A whole-second UTC date, a date without time, or a value both read as no date.
    private static readonly string[] Dates =
    {
        Str("2026-05-08T12:16:49Z"), Str("2025-12-31T23:59:59Z"), Str("2026-05-08"), Str("0001-01-01T00:00:00Z"),
        Str(""), Str("not-a-date"), "null",
    };

    // What D2 reads differently (fraction, offset, no offset) and dates that are no text.
    private static readonly string[] DeliberateDates =
    {
        Str("2026-05-08T12:16:49.1234567Z"), Str("2026-05-08T14:16:49+02:00"), Str("2026-05-08T12:16:49"), "20260508", "true",
    };

    private T Pick<T>(params T[] items) => items[_rng.Next(items.Length)];

    /// <summary>One of <paramref name="values"/>; with every value kind, one of those or of <paramref name="deliberate"/>.</summary>
    private string Value(string[] values, params string[] deliberate)
    {
        var i = _rng.Next(values.Length + (_everyValueKind ? deliberate.Length : 0));
        return i < values.Length ? values[i] : deliberate[i - values.Length];
    }

    private bool Chance(int percent) => _rng.Next(100) < percent;

    private static string Str(string s) => Serialized.GetOrAdd(s, value => System.Text.Json.JsonSerializer.Serialize(value));

    private string Ws() => Pick("", "", "", " ", "\n", "\r\n  ", "\t");

    private string Text() => Value(PlainTexts, DeliberateTexts);

    private string Url() => Pick(
        Str("https://graph.microsoft.com/beta/next?$skiptoken=abc"), Str("https://graph.microsoft.com/v1.0/x?$top=5"));

    private string DateValue() => Value(Dates, DeliberateDates);

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

    private string Matching(string target) => Value(
        new[] { Str(target), Str(target.ToLowerInvariant()), Str("  " + target + " "), Str(target + "X"), Str("OTHER"), Str(""), "12345", "true" },
        "null", "{}", "[" + Str(target) + "]", "\"" + target + "\\ud800\"");

    public string TokenBody() => Root(Obj(
        Prop("access_token", Value(new[] { Str("eyJ0eXAi.token"), Str("t"), Str(""), "null" }, "42", "{}", "\"t\\ud800\"")),
        Prop("expires_in", Value(
            new[] { "3599", "0", "-5", "7200", "9223372036854775807", "\"3599\"", "\" 3599 \"", "\"+3599\"", "\"-1\"", "null" },
            "\"abc\"", "\"\"", "3599.5", "7e2", "true", "99999999999999999999", "{}", "[]", "\"\\ud800\"")),
        Prop("token_type", Str("Bearer"))));

    public string EntraBody() => Root(Obj(
        Prop("error", Pick(Str("invalid_client"), Str("unauthorized_client"))),
        Prop("error_description", Value(
            new[]
            {
                Str("AADSTS7000229: The client application is missing a service principal.\r\nTrace ID: x"),
                Str("AADSTS53003: Access has been blocked by Conditional Access policies."), Str("no code here"), Str("AADSTS: no digits"),
                Str(new string('d', 400)), Str("   "), "null", "42", "true", "{}", "[]",
            },
            Str("2026-11-01T08:00:00Z"), "\"AADSTS53003: broken \\ud800\"")),
        Prop("error_codes", Value(
            new[] { "[7000229]", "[53003,7000229]", "[\"7000229\",53003]", "[7000229.5]", "[7e6]", "[-1]", "[]", "7000229", "null", "[" + _rng.Next(1, 99_999_999) + "]" },
            "[99999999999999999999,53003]", "[1e3]")),
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
        var identifier = Value(
            new[]
            {
                Str("Microsoft Corporation,Virtual Machine,1234-5678"), Str("MICROSOFTCORPORATION,VIRTUALMACHINE,12345678"),
                Str("microsoft corporation, virtual machine, 1234 5678"), Str("Other,Model,1"), Str(""), "null", "42",
            },
            "[" + Str("Microsoft Corporation,Virtual Machine,1234-5678") + "]", "true", "\"Microsoft Corporation,Virtual Machine,1234-5678\\ud800\"");
        return Root(Obj(
            Prop("value", ValueList(() => Obj(
                Prop("importedDeviceIdentityType", Value(new[] { Str("manufacturerModelSerial"), Str("MANUFACTURERMODELSERIAL"), Str("serialNumber"), "null" }, "42")),
                Prop("importedDeviceIdentifier", identifier))), 5),
            Prop("@odata.nextLink", Value(new[] { Url(), Str(""), "null" }, "5", "true", "{}", "\"https://graph.microsoft.com/beta/next\\ud800\""), 50)));
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
        Prop("azureADRegistered", Value(new[] { "true", "false", "null", "\"true\"", "1" }, "\"false\"", "0", "{}")),
        Prop("enrolledDateTime", DateValue())));

    public string ScriptPageBody() => Root(Obj(
        Prop("value", ValueList(() => Obj(
            Prop("id", Value(new[] { Str("s1"), Str("4f9e2c1a-0000-0000-0000-000000000001"), Str(""), "null" }, "42", "{}", "\"s1\\ud800\""), 10),
            Prop("displayName", Text()),
            Prop("fileName", Text()))), 5),
        Prop("@odata.nextLink", Value(new[] { Url(), Str(""), "null" }, "5", "{}", "\"https://graph.microsoft.com/beta/p2\\ud800\""), 50)));

    public string ScriptNameBody() => Root(Obj(
        Prop("id", Str("s1")),
        Prop("displayName", Text()),
        Prop("fileName", Text())));
}
