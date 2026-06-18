"use client";

import { useState } from "react";

interface AutopilotValidationSectionProps {
  validateAutopilotDevice: boolean;
  setValidateAutopilotDevice: (value: boolean) => void;
  validateCorporateIdentifier: boolean;
  setValidateCorporateIdentifier: (value: boolean) => void;
  /** DevPrep "Device association" — shadow mode during Private Preview. Provided when `showDeviceAssociationToggle=true`. */
  validateDeviceAssociation?: boolean;
  /** Toggle + persist DevPrep validation in one shot (no consent dialog). */
  onToggleDeviceAssociation?: (newValue: boolean) => void | Promise<void>;
  /** Render the DevPrep Device Association toggle. Gated to Global Admins by the parent. */
  showDeviceAssociationToggle?: boolean;
  autopilotConsentInProgress: boolean;
  saving: boolean;
  onBeginConsent: (trigger: 'autopilot' | 'corporate' | 'device-preparation') => void;
  /**
   * Probe for pre-approved consent and enable the given validation without the redirect — for
   * admins without consent rights whose tenant already had the app approved by someone else.
   */
  onDetectExistingAccess?: (trigger: 'autopilot' | 'corporate' | 'device-preparation') => void | Promise<void>;
}

export default function AutopilotValidationSection({
  validateAutopilotDevice,
  setValidateAutopilotDevice,
  validateCorporateIdentifier,
  setValidateCorporateIdentifier,
  validateDeviceAssociation = false,
  onToggleDeviceAssociation,
  showDeviceAssociationToggle = false,
  autopilotConsentInProgress,
  saving,
  onBeginConsent,
  onDetectExistingAccess,
}: AutopilotValidationSectionProps) {
  const anyValidationEnabled = validateAutopilotDevice || validateCorporateIdentifier;
  const [disableConfirm, setDisableConfirm] = useState<'autopilot' | 'corporate' | null>(null);

  const handleToggleAutopilot = () => {
    if (validateAutopilotDevice) {
      setDisableConfirm('autopilot');
    } else {
      if (validateCorporateIdentifier) {
        setValidateAutopilotDevice(true);
      } else {
        onBeginConsent('autopilot');
      }
    }
  };

  const handleToggleCorporate = () => {
    if (validateCorporateIdentifier) {
      setDisableConfirm('corporate');
    } else {
      if (validateAutopilotDevice) {
        setValidateCorporateIdentifier(true);
      } else {
        onBeginConsent('corporate');
      }
    }
  };

  const confirmDisable = () => {
    if (disableConfirm === 'autopilot') {
      setValidateAutopilotDevice(false);
    } else if (disableConfirm === 'corporate') {
      setValidateCorporateIdentifier(false);
    }
    setDisableConfirm(null);
  };

  // Per-validation "detect existing access" affordance — probes for pre-approved consent and
  // enables that specific validation without the redirect. Shown under a disabled toggle.
  const renderDetectButton = (trigger: 'autopilot' | 'corporate' | 'device-preparation') =>
    onDetectExistingAccess ? (
      <button
        type="button"
        onClick={() => { void onDetectExistingAccess(trigger); }}
        disabled={saving || autopilotConsentInProgress}
        className="text-xs font-medium text-amber-700 hover:text-amber-800 underline underline-offset-2 disabled:opacity-60 disabled:cursor-not-allowed"
      >
        Detect existing access
      </button>
    ) : null;

  return (
    <div className="bg-white rounded-lg shadow">
      <div className="p-6 border-b border-gray-200 bg-gradient-to-r from-amber-50 to-orange-50">
        <div className="flex items-center justify-between">
          <div className="flex items-center space-x-2">
            <svg className="w-6 h-6 text-amber-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 12l2 2 4-4m5.618-4.016A11.955 11.955 0 0112 2.944a11.955 11.955 0 01-8.618 3.04A12.02 12.02 0 003 9c0 5.591 3.824 10.29 9 11.622 5.176-1.332 9-6.03 9-11.622 0-1.042-.133-2.052-.382-3.016z" />
            </svg>
            <div>
              <h2 className="text-xl font-semibold text-gray-900">
                Enrollment Device Validation
              </h2>
              <p className="text-sm text-gray-500 mt-1">
                Validate devices against Intune registrations before accepting agent data (mandatory for agent ingestion)
              </p>
            </div>
          </div>
          <span className={`flex-shrink-0 inline-flex items-center px-3 py-1 rounded-full text-xs font-medium ${anyValidationEnabled ? "bg-green-100 text-green-800" : "bg-red-100 text-red-800"}`}>
            {anyValidationEnabled ? "Enabled" : "Disabled"}
          </span>
        </div>
      </div>
      <div className="p-6 space-y-5">
        <div className="bg-gray-50 border border-gray-200 rounded-lg p-3 space-y-2">
          <p className="text-sm text-gray-700">
            These validations require the <strong>DeviceManagementServiceConfig.Read.All</strong> permission. Enabling an option starts Microsoft Entra admin consent if not already granted. After consent, the setting is saved automatically. Granting consent requires at least the <strong>Application Administrator</strong> or <strong>Global Administrator</strong> Entra role.
          </p>
          {onDetectExistingAccess && (
            <p className="text-sm text-gray-700">
              Already approved by your organization? In larger tenants the app is often pre-approved by someone with consent rights. If so, use <strong>Detect existing access</strong> under a disabled option to enable it without running the consent flow.
            </p>
          )}
        </div>

        {/* Windows Autopilot (v1) */}
        <div className="space-y-3">
          <p className="text-sm font-semibold text-gray-700 tracking-wide">Windows Autopilot</p>
          <label className="flex items-start justify-between gap-4">
            <div>
              <p className="text-sm font-medium text-gray-900">Enable Autopilot Device Validation</p>
              <p className="text-sm text-gray-500">
                Validates whether the device is registered as a Windows Autopilot device in the tenant.
              </p>
            </div>
            <button
              onClick={handleToggleAutopilot}
              disabled={saving || autopilotConsentInProgress}
              className={`relative inline-flex h-6 w-11 shrink-0 items-center rounded-full transition-colors disabled:opacity-60 disabled:cursor-not-allowed ${validateAutopilotDevice ? 'bg-emerald-500' : 'bg-gray-300'}`}
            >
              <span className={`inline-block h-4 w-4 transform rounded-full bg-white transition-transform ${validateAutopilotDevice ? 'translate-x-6' : 'translate-x-1'}`} />
            </button>
          </label>
          {!validateAutopilotDevice && renderDetectButton('autopilot')}
        </div>

        {/* Windows Autopilot Device Preparation (v2) */}
        <div className="border-t border-gray-100 pt-5 space-y-3">
          <p className="text-sm font-semibold text-gray-700 tracking-wide">Windows Autopilot Device Preparation</p>
          <label className="flex items-start justify-between gap-4">
            <div>
              <p className="text-sm font-medium text-gray-900">Enable Corporate Identifier Validation</p>
              <p className="text-sm text-gray-500">
                Validates devices against Intune Corporate Device Identifiers (manufacturer + model + serial number).
              </p>
            </div>
            <button
              onClick={handleToggleCorporate}
              disabled={saving || autopilotConsentInProgress}
              className={`relative inline-flex h-6 w-11 shrink-0 items-center rounded-full transition-colors disabled:opacity-60 disabled:cursor-not-allowed ${validateCorporateIdentifier ? 'bg-emerald-500' : 'bg-gray-300'}`}
            >
              <span className={`inline-block h-4 w-4 transform rounded-full bg-white transition-transform ${validateCorporateIdentifier ? 'translate-x-6' : 'translate-x-1'}`} />
            </button>
          </label>
          {!validateCorporateIdentifier && renderDetectButton('corporate')}
        </div>

        {/* DevPrep Device Association — Private Preview, GA-gated, shadow-mode (no enrollment block) */}
        {showDeviceAssociationToggle && onToggleDeviceAssociation && (
          <div className="border-t border-gray-100 pt-5 space-y-3" data-testid="devprep-association-toggle">
            <div className="flex items-center gap-2">
              <p className="text-sm font-semibold text-gray-700 tracking-wide">Windows Autopilot Device Preparation</p>
              <span className="inline-flex items-center px-2 py-0.5 rounded-full text-[10px] font-semibold uppercase tracking-wider bg-purple-100 text-purple-800">
                Preview · GA only
              </span>
            </div>
            <label className="flex items-start justify-between gap-4">
              <div>
                <p className="text-sm font-medium text-gray-900">Enable DevPrep Device Association Validation</p>
                <p className="text-sm text-gray-500">
                  Looks devices up in the WDP <code className="text-xs">tenantAssociatedDevices</code> catalog. Currently runs in <strong>shadow mode</strong> — the result is recorded as request telemetry only and does NOT block enrollment. Will become a hard gate once DevPrep ships GA.
                </p>
              </div>
              <button
                onClick={() => { void onToggleDeviceAssociation(!validateDeviceAssociation); }}
                disabled={saving}
                aria-label="Toggle DevPrep Device Association validation"
                className={`relative inline-flex h-6 w-11 shrink-0 items-center rounded-full transition-colors disabled:opacity-60 disabled:cursor-not-allowed ${validateDeviceAssociation ? 'bg-emerald-500' : 'bg-gray-300'}`}
              >
                <span className={`inline-block h-4 w-4 transform rounded-full bg-white transition-transform ${validateDeviceAssociation ? 'translate-x-6' : 'translate-x-1'}`} />
              </button>
            </label>
            {!validateDeviceAssociation && renderDetectButton('device-preparation')}
          </div>
        )}

        <div className="bg-amber-50 border border-amber-200 rounded-lg p-3">
          <p className="text-sm text-amber-900">
            <strong>Important:</strong>{" "}
            If both validations are disabled, backend agent endpoints reject requests for this tenant. Enable at least one and complete admin consent first.
          </p>
        </div>

        {autopilotConsentInProgress && (
          <div className="bg-blue-50 border border-blue-200 rounded-lg p-3 text-sm text-blue-800">
            Checking or applying admin consent...
          </div>
        )}
      </div>

      {/* Disable Validation Confirmation Dialog */}
      {disableConfirm && (
        <div className="fixed inset-0 bg-black bg-opacity-50 flex items-center justify-center z-50 p-4">
          <div className="bg-white rounded-lg shadow-xl max-w-md w-full p-6">
            <div className="flex items-center space-x-3 mb-4">
              <div className="w-12 h-12 bg-amber-100 rounded-full flex items-center justify-center flex-shrink-0">
                <svg className="w-6 h-6 text-amber-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-3L13.732 4c-.77-1.333-2.694-1.333-3.464 0L3.34 16c-.77 1.333.192 3 1.732 3z" />
                </svg>
              </div>
              <div>
                <h3 className="text-lg font-bold text-gray-900">Disable Validation</h3>
                <p className="text-sm text-amber-600 font-medium">
                  {disableConfirm === 'autopilot' ? 'Autopilot Device Validation' : 'Corporate Identifier Validation'}
                </p>
              </div>
            </div>

            <p className="text-sm text-gray-700 mb-2">
              Are you sure you want to disable this validation?
            </p>
            {disableConfirm === 'autopilot' && !validateCorporateIdentifier && (
              <div className="bg-red-50 border border-red-200 rounded-lg p-3 mb-4 text-sm text-red-800">
                This is the last active validation. Disabling it will cause the backend to <strong>reject all agent requests</strong> for this tenant.
              </div>
            )}
            {disableConfirm === 'corporate' && !validateAutopilotDevice && (
              <div className="bg-red-50 border border-red-200 rounded-lg p-3 mb-4 text-sm text-red-800">
                This is the last active validation. Disabling it will cause the backend to <strong>reject all agent requests</strong> for this tenant.
              </div>
            )}

            <div className="flex space-x-3 mt-4">
              <button
                onClick={() => setDisableConfirm(null)}
                className="flex-1 px-4 py-2 border border-gray-300 rounded-md text-gray-700 bg-white hover:bg-gray-50 transition-colors"
              >
                Cancel
              </button>
              <button
                onClick={confirmDisable}
                className="flex-1 px-4 py-2 bg-amber-600 text-white rounded-md hover:bg-amber-700 transition-colors"
              >
                Disable
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
