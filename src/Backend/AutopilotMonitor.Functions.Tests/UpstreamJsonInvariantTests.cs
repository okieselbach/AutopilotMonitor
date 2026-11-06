using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services.GraphResolution;
using Xunit;
using static AutopilotMonitor.Functions.Tests.UpstreamParserReadings;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Invariant fuzz for the Graph and Entra token-endpoint parsers (D-420), without a reference. Over bodies of
/// every value kind, the inputs of D1 to D7 included (<see cref="UpstreamResponseCases"/>), each parser throws
/// only what its callers expect, reads a body the same way however it is laid out, and returns no text the body
/// did not send; <see cref="UpstreamJson.ParseObjectOrNull"/> yields an object, null or a JsonException for any
/// text. Permanent: 500 cases per parser and check.
/// </summary>
public class UpstreamJsonInvariantTests
{
    private const int CaseCount = 500;

    /// <summary>An exception kind a parser's callers expect, named for the failure message.</summary>
    private sealed record Thrown(string Name, Func<Exception, bool> Matches);

    // The callers catch JsonException or everything; an unreadable body or next link throws it.
    private static readonly Thrown Unreadable = new("JsonException", ex => ex is JsonException);

    // A list entry that is no object before the match: the validators answer transient, as before the move.
    // Only the exact type counts; ObjectDisposedException derives from it and would mean a disposed document.
    private static readonly Thrown NonObjectEntry = new("InvalidOperationException", ex => ex.GetType() == typeof(InvalidOperationException));

    /// <summary>A parser as the fuzz drives it.</summary>
    /// <param name="Seed">The generator seed; each check offsets it to see other bodies.</param>
    /// <param name="Body">The body generator for this parser.</param>
    /// <param name="Read">The canonical reading of a body.</param>
    /// <param name="Texts">Every text the result took from the body.</param>
    /// <param name="MayThrow">The exception kinds that may escape; every one of them must also occur.</param>
    private sealed record Parser(
        int Seed,
        Func<UpstreamResponseCases, string> Body,
        Func<string, string> Read,
        Func<string, IEnumerable<string?>> Texts,
        Thrown[] MayThrow);

    private static readonly Dictionary<string, Parser> Parsers = new()
    {
        ["token"] = new(4301, g => g.TokenBody(),
            b => Describe(GraphTokenService.ReadTokenResponse(b)),
            b => new[] { GraphTokenService.ReadTokenResponse(b).AccessToken },
            new[] { Unreadable }),
        ["entra"] = new(4302, g => g.EntraBody(),
            b => Describe(EntraTokenRejection.Parse(400, b)),
            b => new[] { EntraTokenRejection.Parse(400, b).Description },
            Array.Empty<Thrown>()),
        ["autopilot"] = new(4303, g => g.AutopilotBody(Serial),
            b => Describe(AutopilotDeviceValidator.FindRegistration(b, Serial)),
            b => AutopilotDeviceValidator.FindRegistration(b, Serial) is { } r
                ? new[] { r.DeviceId, r.GroupTag, r.PurchaseOrderId, r.DisplayName }
                : Array.Empty<string?>(),
            new[] { Unreadable, NonObjectEntry }),
        ["corporate"] = new(4304, g => g.CorporateBody(),
            b => Describe(CorporateIdentifierValidator.ReadIdentityPage(b, CorporateId)),
            // An empty next url is the parser's own "no further page", not text from the body.
            b => new[] { CorporateIdentifierValidator.ReadIdentityPage(b, CorporateId).NextUrl is { Length: > 0 } next ? next : null },
            new[] { Unreadable, NonObjectEntry }),
        ["association"] = new(4305, g => g.AssociationBody(Serial),
            b => Describe(DeviceAssociationValidator.ParseTenantAssociatedDevicesResponse(b, Serial)),
            b =>
            {
                var r = DeviceAssociationValidator.ParseTenantAssociatedDevicesResponse(b, Serial);
                return new[] { r.AssociationState, r.DevicePreparationPolicyId, r.PreAssociatedByUserPrincipalName, r.AssignedToUserPrincipalName, r.ManagedDeviceId };
            },
            new[] { NonObjectEntry }),
        ["cloudpc"] = new(4306, g => g.CloudPcBody(IntuneId),
            b => Describe(CloudPcDeviceValidator.ParseCloudPcResponse(b, IntuneId)),
            b =>
            {
                var r = CloudPcDeviceValidator.ParseCloudPcResponse(b, IntuneId);
                return new[] { r.CloudPcId, r.ManagedDeviceName, r.ServicePlanName };
            },
            new[] { NonObjectEntry }),
        ["managed-device"] = new(4307, g => g.ManagedDeviceBody(IntuneId),
            b => Describe(IntuneDeviceBindingValidator.ParseManagedDeviceResponse(b, IntuneId)),
            b =>
            {
                var r = IntuneDeviceBindingValidator.ParseManagedDeviceResponse(b, IntuneId);
                return new[] { r.DeviceName, r.AzureAdDeviceId, r.ManagementState, r.SerialNumber, r.OwnerType, r.EnrollmentType };
            },
            Array.Empty<Thrown>()),
        ["script-page"] = new(4308, g => g.ScriptPageBody(),
            b => Describe(ScriptDisplayNameResolver.ReadScriptPage(b, withFileName: true)),
            b =>
            {
                var (scripts, next) = ScriptDisplayNameResolver.ReadScriptPage(b, withFileName: true);
                return scripts.SelectMany(s => new[] { s.Id, s.DisplayName, s.FileName }).Append(next);
            },
            new[] { Unreadable }),
        ["script-name"] = new(4309, g => g.ScriptNameBody(),
            b => Describe(ScriptDisplayNameResolver.ReadScriptDisplayName(b)),
            b => new[] { ScriptDisplayNameResolver.ReadScriptDisplayName(b) },
            new[] { Unreadable }),
    };

    public static IEnumerable<object[]> ParserNames => Parsers.Keys.Select(name => new object[] { name });

    [Theory]
    [MemberData(nameof(ParserNames))]
    public void A_parser_throws_only_what_its_callers_expect(string name)
    {
        var parser = Parsers[name];
        var cases = new UpstreamResponseCases(parser.Seed, everyValueKind: true);
        var seen = new HashSet<string>();
        for (var i = 0; i < CaseCount; i++)
        {
            var body = parser.Body(cases);
            try
            {
                parser.Read(body);
            }
            catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
            {
                var kind = parser.MayThrow.FirstOrDefault(t => t.Matches(ex));
                Assert.True(kind != null, $"{name}, case {i}: {ex.GetType().Name} escapes ({ex.Message}) for body {body}");
                seen.Add(kind!.Name);
            }
        }

        // Every listed kind still occurs, so the list cannot quietly outgrow what the parser does.
        Assert.Equal(parser.MayThrow.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal), seen.OrderBy(n => n, StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(ParserNames))]
    public void Layout_never_changes_what_a_parser_reads(string name)
    {
        var parser = Parsers[name];
        var cases = new UpstreamResponseCases(parser.Seed + 100, everyValueKind: true);
        var layout = new Random(parser.Seed);
        var compared = 0;
        for (var i = 0; i < CaseCount; i++)
        {
            var body = parser.Body(cases);
            if (ReadableObject(body) is not { } root)
                continue;

            var expected = Outcome(() => parser.Read(body));
            foreach (var spread in new[] { false, true })
            {
                var twin = Render(root, layout, spread);
                var actual = Outcome(() => parser.Read(twin));
                Assert.True(expected == actual, $"{name}, case {i}: body {body}\n  laid out as {twin}\n  reads {actual}\n  instead of {expected}");
            }
            compared++;
        }

        Assert.True(compared >= CaseCount / 2, $"only {compared} of {CaseCount} bodies were readable objects");
    }

    [Theory]
    [MemberData(nameof(ParserNames))]
    public void A_parser_returns_only_text_the_body_sent(string name)
    {
        var parser = Parsers[name];
        var cases = new UpstreamResponseCases(parser.Seed + 200, everyValueKind: true);
        var checkedTexts = 0;
        for (var i = 0; i < CaseCount; i++)
        {
            var body = parser.Body(cases);
            string?[] texts;
            try
            {
                texts = parser.Texts(body).ToArray();
            }
            catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
            {
                continue; // which exceptions may escape is the first check's business
            }

            var sent = ReadableObject(body) is { } root ? SentTexts(root) : new List<string>();
            foreach (var text in texts.OfType<string>())
            {
                // Trimming and cutting keep a text inside what was sent; an empty text needs a blank one.
                var fromBody = text.Length == 0
                    ? sent.Any(string.IsNullOrWhiteSpace)
                    : sent.Any(s => s.Contains(text, StringComparison.Ordinal));
                Assert.True(fromBody, $"{name}, case {i}: \"{text}\" is no text the body sent: {body}");
                checkedTexts++;
            }
        }

        // The corporate page returns its next url as the only text, so the bar is low.
        Assert.True(checkedTexts >= CaseCount / 50, $"only {checkedTexts} texts came back from {CaseCount} bodies");
    }

    [Fact]
    public void ParseObjectOrNull_yields_an_object_or_null_or_throws_JsonException_for_any_text()
    {
        var edits = new Random(4310);
        int objects = 0, nulls = 0, refused = 0;
        foreach (var parser in Parsers.Values)
        {
            var cases = new UpstreamResponseCases(parser.Seed + 300, everyValueKind: true);
            for (var i = 0; i < CaseCount / 2; i++)
            {
                var body = parser.Body(cases);
                foreach (var text in new[] { body, Mutate(body, edits) })
                {
                    try
                    {
                        if (UpstreamJson.ParseObjectOrNull(text) is { } json)
                        {
                            Assert.Equal(JsonValueKind.Object, json.ValueKind);
                            objects++;
                        }
                        else
                        {
                            nulls++;
                        }
                    }
                    catch (JsonException)
                    {
                        refused++;
                    }
                    catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
                    {
                        Assert.Fail($"{ex.GetType().Name} ({ex.Message}) for text {text}");
                    }
                }
            }
        }

        Assert.True(objects > 0 && nulls > 0 && refused > 0, $"objects {objects}, nulls {nulls}, refused {refused}");
    }

    private static JsonElement? ReadableObject(string body)
    {
        try
        {
            return UpstreamJson.ParseObjectOrNull(body);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Every string value in the body, decoded; one whose escapes are no valid UTF-16 is skipped.</summary>
    private static List<string> SentTexts(JsonElement root)
    {
        var texts = new List<string>();
        Collect(root);
        return texts;

        void Collect(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var member in element.EnumerateObject())
                        Collect(member.Value);
                    break;
                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                        Collect(item);
                    break;
                case JsonValueKind.String:
                    try { texts.Add(element.GetString()!); }
                    catch (InvalidOperationException) { }
                    break;
            }
        }
    }

    /// <summary>
    /// The body written again in another layout: compact, or spread out with block and line comments and
    /// trailing commas. Object members change their order unless a name repeats (the last one must stay last);
    /// values keep their raw text, escapes and number form included.
    /// </summary>
    private static string Render(JsonElement root, Random rng, bool spread)
    {
        var text = new StringBuilder();
        Write(root);
        return text.ToString();

        void Write(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    var members = element.EnumerateObject().ToList();
                    if (members.Select(m => m.Name).Distinct(StringComparer.Ordinal).Count() == members.Count)
                        members = members.OrderBy(_ => rng.Next()).ToList();
                    text.Append(spread ? "{ /* object */\n  " : "{");
                    for (var i = 0; i < members.Count; i++)
                    {
                        if (i > 0)
                            text.Append(spread ? (rng.Next(2) == 0 ? ",// next\n  " : " ,\t/* next */ ") : ",");
                        text.Append(JsonSerializer.Serialize(members[i].Name)).Append(spread ? " : " : ":");
                        Write(members[i].Value);
                    }
                    text.Append(spread && members.Count > 0 ? ",\n}" : "}");
                    break;
                case JsonValueKind.Array:
                    var items = element.EnumerateArray().ToList();
                    text.Append(spread ? "[\n  " : "[");
                    for (var i = 0; i < items.Count; i++)
                    {
                        if (i > 0)
                            text.Append(spread ? " , " : ",");
                        Write(items[i]);
                    }
                    text.Append(spread && items.Count > 0 ? ",\n]" : "]");
                    break;
                default:
                    text.Append(element.GetRawText());
                    break;
            }
        }
    }

    /// <summary>One random edit: a character removed, inserted or replaced, or the text cut short.</summary>
    private static string Mutate(string text, Random rng)
    {
        if (text.Length == 0)
            return "{";
        const string alphabet = "{}[]\",:/*\\ \n0123456789-+.eEtrufalsné";
        var at = rng.Next(text.Length);
        var c = alphabet[rng.Next(alphabet.Length)].ToString();
        return rng.Next(4) switch
        {
            0 => text.Remove(at, 1),
            1 => text.Insert(at, c),
            2 => text.Substring(0, at),
            _ => text.Remove(at, 1).Insert(at, c),
        };
    }
}
