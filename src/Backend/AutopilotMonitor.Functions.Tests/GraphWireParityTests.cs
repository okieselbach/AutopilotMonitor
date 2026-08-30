using System.Text.Json;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Wire-identity proof for the Graph function folder (anonymous-object → typed-DTO
/// migration). Each fact serializes the OLD anonymous literal exactly as it stood at the
/// call site (copied from the pre-migration code, filled with realistic sample values)
/// against the NEW DTO carrying the same values, via
/// <see cref="ApiResponseWireParityTests.AssertWireIdentical"/> — key names, key order and
/// key presence/absence (WhenWritingNull) must match ordinally. Nullable slots additionally
/// get a null case proving the key vanishes identically on both sides.
/// </summary>
public class GraphWireParityTests
{
    // ---- GetScriptDisplayNames -----------------------------------------------------------

    [Fact]
    public void GetScriptDisplayNamesResponse_matches_the_resolved_shape_with_malformed_tokens()
    {
        // Unresolved refs keep their key with an explicit null VALUE — dictionary values are
        // not subject to WhenWritingNull (only properties are), on both sides.
        var payload = new Dictionary<string, string?>
        {
            ["Platform:1f9d3a52-4c1e-4b7a-9a01-52f3aaaa1001"] = "Install VPN client",
            ["Remediation:2eab4b63-5d2f-4c8b-8b12-52f3aaaa1002"] = null,
        };
        var malformed = new List<string> { "NotARef", "Platform:" };

        AssertParity(
            new
            {
                refs = payload,
                malformed = malformed.Count > 0 ? (object)malformed : null,
            },
            new GetScriptDisplayNamesResponse
            {
                Refs = payload,
                Malformed = malformed,
            });
    }

    [Fact]
    public void GetScriptDisplayNamesResponse_omits_malformed_when_all_tokens_parsed()
    {
        var payload = new Dictionary<string, string?>
        {
            ["Platform:3fbc5c74-6e30-4d9c-9c23-52f3aaaa1003"] = "Set timezone",
        };
        var malformed = new List<string>();

        AssertParity(
            new
            {
                refs = payload,
                malformed = malformed.Count > 0 ? (object)malformed : null,
            },
            new GetScriptDisplayNamesResponse
            {
                Refs = payload,
                Malformed = malformed.Count > 0 ? malformed : null,
            });
    }

    [Fact]
    public void GetScriptDisplayNamesResponse_matches_the_empty_body_early_exit_shape()
    {
        // Old early-exit sites (empty body / empty refs list) never declared a malformed key
        // at all: `new { refs = new Dictionary<string, string?>() }`. The typed side leaves
        // Malformed null so WhenWritingNull removes the key identically.
        AssertParity(
            new { refs = new Dictionary<string, string?>() },
            new GetScriptDisplayNamesResponse { Refs = new Dictionary<string, string?>() });
    }

    [Fact]
    public void GetScriptDisplayNamesResponse_dictionary_keys_are_not_camel_cased()
    {
        // ApiJsonOptions sets PropertyNamingPolicy = CamelCase but NO DictionaryKeyPolicy:
        // property names camel-case, dictionary keys (canonical script refs with a PascalCase
        // type prefix) serialize verbatim. Pin that asymmetry so a future serializer change
        // (e.g. adding DictionaryKeyPolicy = CamelCase) fails loudly here.
        var typed = new GetScriptDisplayNamesResponse
        {
            Refs = new Dictionary<string, string?>
            {
                ["Platform:4acd6d85-7f41-4ead-8d34-52f3aaaa1004"] = "Map drives",
                ["Remediation:5bde7e96-8052-4fbe-9e45-52f3aaaa1005"] = null,
            },
        };

        var json = JsonSerializer.Serialize(typed, ApiJsonOptions.Create());

        Assert.Contains("\"refs\":", json); // property name IS camel-cased
        Assert.Contains("\"Platform:4acd6d85-7f41-4ead-8d34-52f3aaaa1004\"", json);
        Assert.Contains("\"Remediation:5bde7e96-8052-4fbe-9e45-52f3aaaa1005\":null", json);
        Assert.DoesNotContain("\"platform:", json);
        Assert.DoesNotContain("\"remediation:", json);
    }

    // ---- GetGraphPermissionsStatus -------------------------------------------------------

    [Fact]
    public void GetGraphPermissionsStatusResponse_matches_the_authoritative_shape()
    {
        var clientId = "6cef8fa7-9163-40cf-af56-52f3aaaa1006";
        var grantedRoles = new[] { "DeviceManagementConfiguration.Read.All" };
        var features = new List<GraphFeatureStatusItem>
        {
            new GraphFeatureStatusItem
            {
                Name = "ScriptDisplayNames",
                Granted = true,
                RequiredPermissions = new[] { "DeviceManagementConfiguration.Read.All" },
            },
            new GraphFeatureStatusItem
            {
                Name = "W365CloudPcValidation",
                Granted = false,
                RequiredPermissions = new[] { "CloudPC.Read.All" },
            },
        };
        var anonymousFeatures = new List<object>
        {
            new
            {
                name = "ScriptDisplayNames",
                granted = (bool?)true,
                requiredPermissions = (IReadOnlyList<string>)new[] { "DeviceManagementConfiguration.Read.All" },
            },
            new
            {
                name = "W365CloudPcValidation",
                granted = (bool?)false,
                requiredPermissions = (IReadOnlyList<string>)new[] { "CloudPC.Read.All" },
            },
        };

        AssertParity(
            new
            {
                clientId,
                isTransient = false,
                grantedRoles,
                features = anonymousFeatures,
            },
            new GetGraphPermissionsStatusResponse
            {
                ClientId = clientId,
                IsTransient = false,
                GrantedRoles = grantedRoles,
                Features = features,
            });
    }

    [Fact]
    public void GetGraphPermissionsStatusResponse_omits_granted_on_a_transient_snapshot()
    {
        // Old site: `granted = snapshot.IsTransient ? (bool?)null : ...` — WhenWritingNull
        // removed the key per feature row; the typed bool? must vanish identically.
        var anonymousFeatures = new List<object>
        {
            new
            {
                name = "ScriptDisplayNames",
                granted = (bool?)null,
                requiredPermissions = (IReadOnlyList<string>)new[] { "DeviceManagementConfiguration.Read.All" },
            },
        };
        var typedFeatures = new List<GraphFeatureStatusItem>
        {
            new GraphFeatureStatusItem
            {
                Name = "ScriptDisplayNames",
                Granted = null,
                RequiredPermissions = new[] { "DeviceManagementConfiguration.Read.All" },
            },
        };

        AssertParity(
            new
            {
                clientId = string.Empty,
                isTransient = true,
                grantedRoles = Array.Empty<string>(),
                features = anonymousFeatures,
            },
            new GetGraphPermissionsStatusResponse
            {
                ClientId = string.Empty,
                IsTransient = true,
                GrantedRoles = Array.Empty<string>(),
                Features = typedFeatures,
            });
    }

    // ---- RefreshGraphPermissions ---------------------------------------------------------

    [Fact]
    public void RefreshGraphPermissions_success_body_matches_SuccessOnlyResponse()
    {
        AssertParity(
            new { success = true },
            new SuccessOnlyResponse { Success = true });
    }

    private static void AssertParity(object anonymousLiteral, IApiResponse typed)
        => ApiResponseWireParityTests.AssertWireIdentical(anonymousLiteral, typed);
}
