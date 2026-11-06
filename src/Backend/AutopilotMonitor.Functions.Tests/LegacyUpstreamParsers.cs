using System;
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services.GraphResolution;
using AutopilotMonitor.Shared.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// The Graph and Entra token-endpoint response parsers as they read with Newtonsoft before D-420, copied
/// verbatim as the reference for <see cref="UpstreamJsonDifferentialTests"/>. Temporary: removed together
/// with that test once the System.Text.Json parsers have run cleanly in production (backlog).
/// </summary>
internal static class LegacyUpstreamParsers
{
    public static (string? AccessToken, long? ExpiresInSeconds) ReadTokenResponse(string responseBody)
    {
        var tokenJson = JsonConvert.DeserializeObject<JObject>(responseBody);
        var accessToken = tokenJson?["access_token"]?.ToString();
        return string.IsNullOrEmpty(accessToken) ? (accessToken, null) : (accessToken, (long?)tokenJson?["expires_in"]);
    }

    public static EntraTokenRejection ParseEntraRejection(int statusCode, string? body)
    {
        JObject? json = null;
        if (!string.IsNullOrWhiteSpace(body))
        {
            try { json = JsonConvert.DeserializeObject<JToken>(body) as JObject; }
            catch (JsonException) { json = null; }
        }
        var description = (json?["error_description"] as JValue)?.Value as string;

        string? code = null;
        var first = (json?["error_codes"] as JArray)?.FirstOrDefault(t => t.Type == JTokenType.Integer);
        if (first != null)
        {
            code = "AADSTS" + first.Value<long>();
        }
        else
        {
            var start = description?.IndexOf("AADSTS", StringComparison.Ordinal) ?? -1;
            if (start >= 0)
            {
                var digits = new string(description!.Skip(start + "AADSTS".Length).TakeWhile(char.IsDigit).ToArray());
                code = digits.Length == 0 ? null : "AADSTS" + digits;
            }
        }

        string? shortened = null;
        if (!string.IsNullOrWhiteSpace(description))
        {
            var line = description.Split('\n')[0].Trim();
            shortened = line.Length <= 300 ? line : line.Substring(0, 300);
        }
        return new EntraTokenRejection(statusCode, code, shortened);
    }

    public static AutopilotRegistration? FindRegistration(string responseBody, string normalizedSerial)
    {
        var data = JsonConvert.DeserializeObject<JObject>(responseBody);
        if (data?["value"] is not JArray devices || devices.Count == 0)
            return null;

        var exactDevice = devices.FirstOrDefault(d => string.Equals(
            d?["serialNumber"]?.ToString()?.Trim(), normalizedSerial, StringComparison.OrdinalIgnoreCase));
        if (exactDevice == null)
            return null;

        return new AutopilotRegistration
        {
            DeviceId = RegistrationText(exactDevice["id"]),
            GroupTag = RegistrationText(exactDevice["groupTag"]),
            PurchaseOrderId = RegistrationText(exactDevice["purchaseOrderIdentifier"]),
            DisplayName = RegistrationText(exactDevice["displayName"]),
            UserAssigned = RegistrationText(exactDevice["userPrincipalName"]) != null,
        };
    }

    private static string? RegistrationText(JToken? token)
    {
        var value = token?.Type == JTokenType.String ? ((string?)token)?.Trim() : null;
        if (string.IsNullOrEmpty(value))
            return null;
        return value!.Length > AutopilotDeviceValidator.MaxRegistrationFieldLength
            ? value.Substring(0, AutopilotDeviceValidator.MaxRegistrationFieldLength)
            : value;
    }

    public static (bool Found, string NextUrl) ReadIdentityPage(string responseBody, string normalizedIdentifier)
    {
        var data = JsonConvert.DeserializeObject<JObject>(responseBody);
        if (data?["value"] is JArray identities)
        {
            foreach (var identity in identities)
            {
                var type = identity?["importedDeviceIdentityType"]?.ToString();
                var value = identity?["importedDeviceIdentifier"]?.ToString();
                if (string.Equals(type, "manufacturerModelSerial", StringComparison.OrdinalIgnoreCase)
                    && value != null
                    && string.Equals(CorporateIdentifierValidator.NormalizeStoredIdentifier(value), normalizedIdentifier, StringComparison.Ordinal))
                {
                    return (true, string.Empty);
                }
            }
        }
        return (false, data?["@odata.nextLink"]?.ToString() ?? string.Empty);
    }

    public static DeviceAssociationResult ParseAssociation(string responseBody, string normalizedSerial)
    {
        var notFound = new DeviceAssociationResult
        {
            IsValid = false,
            SerialNumber = normalizedSerial,
            ErrorMessage = $"Device with serial '{normalizedSerial}' is not associated with this tenant (device association)"
        };

        JObject? data;
        try { data = JsonConvert.DeserializeObject<JObject>(responseBody); }
        catch (JsonException) { return notFound; }

        var devices = data?["value"] as JArray;
        if (devices == null || devices.Count == 0)
            return notFound;

        var match = devices.FirstOrDefault(d => string.Equals(
            d?["serialNumber"]?.ToString()?.Trim(), normalizedSerial, StringComparison.OrdinalIgnoreCase));
        if (match == null)
            return notFound;

        return new DeviceAssociationResult
        {
            IsValid = true,
            SerialNumber = normalizedSerial,
            AssociationState = match["associationState"]?.ToString(),
            DevicePreparationPolicyId = match["devicePreparationPolicyId"]?.ToString(),
            PreAssociatedByUserPrincipalName = match["preassociatedByUserPrincipalName"]?.ToString(),
            AssignedToUserPrincipalName = match["assignedToUserPrincipalName"]?.ToString(),
            PreAssociationDateTime = ParseAssociationDate(match["preassociationDateTime"]),
            AssociationDateTime = ParseAssociationDate(match["associationDateTime"]),
            ManagedDeviceId = match["managedDeviceId"]?.ToString()
        };
    }

    private static DateTime? ParseAssociationDate(JToken? token)
    {
        if (token == null || token.Type == JTokenType.Null) return null;
        var s = token.ToString();
        if (string.IsNullOrEmpty(s)) return null;
        if (DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var dt))
            return dt.Year <= 1 ? null : dt;
        return null;
    }

    public static CloudPcValidationResult ParseCloudPc(string responseBody, string normalizedId)
    {
        var notFound = new CloudPcValidationResult
        {
            IsValid = false,
            IntuneDeviceId = normalizedId,
            ErrorMessage = $"Device '{normalizedId}' is not a Windows 365 Cloud PC in this tenant"
        };

        JObject? data;
        try { data = JsonConvert.DeserializeObject<JObject>(responseBody); }
        catch (JsonException) { return notFound; }

        var cloudPcs = data?["value"] as JArray;
        if (cloudPcs == null || cloudPcs.Count == 0)
            return notFound;

        var match = cloudPcs.FirstOrDefault(c => string.Equals(
            c?["managedDeviceId"]?.ToString()?.Trim(), normalizedId, StringComparison.OrdinalIgnoreCase));
        if (match == null)
            return notFound;

        return new CloudPcValidationResult
        {
            IsValid = true,
            IntuneDeviceId = normalizedId,
            CloudPcId = match["id"]?.ToString(),
            ManagedDeviceName = match["managedDeviceName"]?.ToString(),
            ServicePlanName = match["servicePlanName"]?.ToString()
        };
    }

    private static readonly HashSet<string> LeavingManagementStates = new(StringComparer.OrdinalIgnoreCase)
    {
        "retirePending", "retireIssued", "wipePending", "wipeIssued", "deletePending",
    };

    public static IntuneDeviceBindingResult ParseManagedDevice(string responseBody, string normalizedId)
    {
        var notFound = new IntuneDeviceBindingResult
        {
            Outcome = IntuneDeviceBindingOutcome.NotFound,
            IntuneDeviceId = normalizedId,
            ErrorMessage = $"Device '{normalizedId}' is not an enrolled Intune device in this tenant"
        };

        JObject? device;
        try { device = JsonConvert.DeserializeObject<JObject>(responseBody); }
        catch (JsonException) { return notFound; }

        var returnedId = device?["id"]?.ToString()?.Trim();
        if (string.IsNullOrEmpty(returnedId) || !string.Equals(returnedId, normalizedId, StringComparison.OrdinalIgnoreCase))
            return notFound;

        var managementState = device?["managementState"]?.ToString();
        var leaving = managementState != null && LeavingManagementStates.Contains(managementState);

        return new IntuneDeviceBindingResult
        {
            Outcome = leaving ? IntuneDeviceBindingOutcome.NotManaged : IntuneDeviceBindingOutcome.Match,
            IntuneDeviceId = normalizedId,
            DeviceName = device?["deviceName"]?.ToString(),
            AzureAdDeviceId = device?["azureADDeviceId"]?.ToString(),
            ManagementState = managementState,
            SerialNumber = NullIfEmpty(device?["serialNumber"]?.ToString()),
            OwnerType = NullIfEmpty(device?["managedDeviceOwnerType"]?.ToString()),
            EnrollmentType = NullIfEmpty(device?["deviceEnrollmentType"]?.ToString()),
            AzureAdRegistered = device?["azureADRegistered"]?.Type == JTokenType.Boolean
                ? device["azureADRegistered"]!.Value<bool>()
                : null,
            EnrolledDateTime = ParseEnrolledDateTime(device?["enrolledDateTime"]?.ToString()),
            ErrorMessage = leaving
                ? $"Device '{normalizedId}' is being removed from Intune management (state {managementState})"
                : null
        };
    }

    private static DateTimeOffset? ParseEnrolledDateTime(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (!DateTimeOffset.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
            return null;
        return parsed.Year <= 1 ? null : parsed;
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    public static (List<ScriptDisplayNameResolver.ScriptListItem> Scripts, string? NextUrl) ReadScriptPage(string body, bool withFileName)
    {
        var json = JObject.Parse(body);
        var values = json["value"] as JArray ?? new JArray();
        var scripts = new List<ScriptDisplayNameResolver.ScriptListItem>();
        foreach (var item in values.OfType<JObject>())
        {
            var id = item["id"]?.ToString();
            if (string.IsNullOrEmpty(id)) continue;
            scripts.Add(new ScriptDisplayNameResolver.ScriptListItem(id!, item["displayName"]?.ToString(), withFileName ? item["fileName"]?.ToString() : null));
        }
        return (scripts, json["@odata.nextLink"]?.ToString());
    }

    public static string? ReadScriptDisplayName(string body) => JObject.Parse(body)["displayName"]?.ToString();
}
