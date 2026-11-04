using System;
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Models.Graph;

namespace AutopilotMonitor.Functions.Security
{
    /// <summary>
    /// A device validation that needs an optional Graph permission (an add-on grant) instead of the
    /// default consent: its switch, its feature in <see cref="GraphFeatureCatalog"/>, and the two
    /// tenant fields of its missing-permission episode. <see cref="GraphAccessHealthRecorder"/> runs the
    /// episodes in the same two stages as the token state.
    /// </summary>
    public sealed class AddOnValidation
    {
        public static readonly AddOnValidation CloudPc = new(
            GraphFeatureCatalog.FeatureW365CloudPcValidation,
            c => c.ValidateCloudPcDevice,
            c => c.CloudPcPermissionMissingSince, (c, v) => c.CloudPcPermissionMissingSince = v,
            c => c.CloudPcPermissionMissingConfirmedAt, (c, v) => c.CloudPcPermissionMissingConfirmedAt = v);

        public static readonly AddOnValidation IntuneDeviceBinding = new(
            GraphFeatureCatalog.FeatureIntuneDeviceBinding,
            c => c.ValidateIntuneDeviceBinding,
            c => c.IntuneDeviceBindingPermissionMissingSince, (c, v) => c.IntuneDeviceBindingPermissionMissingSince = v,
            c => c.IntuneDeviceBindingPermissionMissingConfirmedAt, (c, v) => c.IntuneDeviceBindingPermissionMissingConfirmedAt = v);

        /// <summary>Both, in chain order.</summary>
        public static readonly IReadOnlyList<AddOnValidation> All = new[] { CloudPc, IntuneDeviceBinding };

        private readonly Func<TenantConfiguration, bool> _isEnabled;
        private readonly Func<TenantConfiguration, DateTime?> _since;
        private readonly Action<TenantConfiguration, DateTime?> _setSince;
        private readonly Func<TenantConfiguration, DateTime?> _confirmedAt;
        private readonly Action<TenantConfiguration, DateTime?> _setConfirmedAt;

        private AddOnValidation(
            string feature,
            Func<TenantConfiguration, bool> isEnabled,
            Func<TenantConfiguration, DateTime?> since, Action<TenantConfiguration, DateTime?> setSince,
            Func<TenantConfiguration, DateTime?> confirmedAt, Action<TenantConfiguration, DateTime?> setConfirmedAt)
        {
            Feature = feature;
            Permission = string.Join(", ", GraphFeatureCatalog.RequiredPermissions(feature));
            _isEnabled = isEnabled;
            _since = since;
            _setSince = setSince;
            _confirmedAt = confirmedAt;
            _setConfirmedAt = setConfirmedAt;
        }

        /// <summary>Feature id of the grant script and of the permission-status endpoint.</summary>
        public string Feature { get; }

        /// <summary>The Graph application permission the add-on grant adds.</summary>
        public string Permission { get; }

        public bool IsEnabled(TenantConfiguration config) => _isEnabled(config);

        public bool IsGranted(IEnumerable<string> roles) => GraphFeatureCatalog.IsFeatureGranted(Feature, roles);

        public DateTime? MissingSince(TenantConfiguration config) => _since(config);

        public DateTime? MissingConfirmedAt(TenantConfiguration config) => _confirmedAt(config);

        internal void StampSince(TenantConfiguration config, DateTime at) => _setSince(config, at);

        internal void StampConfirmed(TenantConfiguration config, DateTime at) => _setConfirmedAt(config, at);

        internal void ClearEpisode(TenantConfiguration config)
        {
            _setSince(config, null);
            _setConfirmedAt(config, null);
        }

        /// <summary>Switched on and its missing permission confirmed: the portal shows the notice.</summary>
        public bool IsConfirmedMissing(TenantConfiguration config) => IsEnabled(config) && MissingConfirmedAt(config) != null;

        /// <summary>
        /// True when every switched-on validation is an add-on whose missing permission is confirmed:
        /// then no device can be validated at all.
        /// </summary>
        public static bool BlocksAllValidation(TenantConfiguration config) =>
            config.HasAnyDeviceValidation()
            && !config.ValidateAutopilotDevice
            && !config.ValidateCorporateIdentifier
            && !config.ValidateDeviceAssociation
            && !All.Any(a => a.IsEnabled(config) && !a.IsConfirmedMissing(config));
    }
}
