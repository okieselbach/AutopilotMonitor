using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services.GraphResolution;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// One canonical text per result of the Graph and Entra token-endpoint parsers (D-420), so two readings of a
/// body compare as strings: the differential test compares production with the Newtonsoft reference, the
/// invariant fuzz compares a body with its reformatted twin.
/// </summary>
internal static class UpstreamParserReadings
{
    internal const string Serial = "PF3XKQ7";
    internal const string IntuneId = "6f1c4d2a-0b3e-4c5d-8e9f-1a2b3c4d5e6f";

    internal static readonly string CorporateId =
        CorporateIdentifierValidator.BuildNormalizedIdentifier("Microsoft Corporation", "Virtual Machine", "1234-5678");

    /// <summary>The reading, or "&lt;throws&gt;" when the parser throws.</summary>
    internal static string Outcome(Func<string> run)
    {
        try { return run(); }
        catch (Exception ex) when (ex is not Xunit.Sdk.XunitException) { return "<throws>"; }
    }

    private static string Text(string? value) => value is null ? "<null>" : "\"" + value + "\"";

    private static string Date(DateTime? value) => value is { } d ? d.ToString("O", CultureInfo.InvariantCulture) + "/" + d.Kind : "<null>";

    // Every consumer reads the token with IsNullOrEmpty, so an empty and an absent token describe alike.
    internal static string Describe((string? AccessToken, long? ExpiresInSeconds) r) =>
        $"token={(string.IsNullOrEmpty(r.AccessToken) ? "<none>" : Text(r.AccessToken))} expires={r.ExpiresInSeconds?.ToString(CultureInfo.InvariantCulture) ?? "<null>"}";

    internal static string Describe(EntraTokenRejection r) => $"status={r.StatusCode} code={Text(r.ErrorCode)} description={Text(r.Description)}";

    internal static string Describe(AutopilotMonitor.Shared.Models.AutopilotRegistration? r) => r is null
        ? "<not registered>"
        : $"id={Text(r.DeviceId)} tag={Text(r.GroupTag)} po={Text(r.PurchaseOrderId)} name={Text(r.DisplayName)} user={r.UserAssigned}";

    internal static string Describe((bool Found, string NextUrl) r) => $"found={r.Found} next={Text(r.NextUrl)}";

    internal static string Describe(DeviceAssociationResult r) =>
        $"valid={r.IsValid} transient={r.IsTransient} serial={Text(r.SerialNumber)} state={Text(r.AssociationState)} policy={Text(r.DevicePreparationPolicyId)} " +
        $"preBy={Text(r.PreAssociatedByUserPrincipalName)} assigned={Text(r.AssignedToUserPrincipalName)} pre={Date(r.PreAssociationDateTime)} " +
        $"at={Date(r.AssociationDateTime)} device={Text(r.ManagedDeviceId)} error={Text(r.ErrorMessage)}";

    internal static string Describe(CloudPcValidationResult r) =>
        $"valid={r.IsValid} transient={r.IsTransient} device={Text(r.IntuneDeviceId)} cpc={Text(r.CloudPcId)} name={Text(r.ManagedDeviceName)} " +
        $"plan={Text(r.ServicePlanName)} error={Text(r.ErrorMessage)}";

    internal static string Describe(IntuneDeviceBindingResult r) =>
        $"outcome={r.Outcome} device={Text(r.IntuneDeviceId)} name={Text(r.DeviceName)} aad={Text(r.AzureAdDeviceId)} state={Text(r.ManagementState)} " +
        $"serial={Text(r.SerialNumber)} owner={Text(r.OwnerType)} enrollment={Text(r.EnrollmentType)} registered={r.AzureAdRegistered?.ToString() ?? "<null>"} " +
        $"enrolled={(r.EnrolledDateTime is { } e ? e.ToString("O", CultureInfo.InvariantCulture) : "<null>")} error={Text(r.ErrorMessage)}";

    // A null or empty next link both end the full pull, so they describe alike.
    internal static string Describe((List<ScriptDisplayNameResolver.ScriptListItem> Scripts, string? NextUrl) r) =>
        string.Join(";", r.Scripts.Select(s => $"{Text(s.Id)}={Text(s.DisplayName)}/{Text(s.FileName)}")) + $" next={Text(string.IsNullOrEmpty(r.NextUrl) ? null : r.NextUrl)}";

    internal static string Describe(string? scriptName) => scriptName ?? "<null>";
}
