"use client";

import { useState } from "react";
import Link from "next/link";
import { SectionCardHeader } from "@/components/SectionCardHeader";
import { DOCS_PATHS } from "@/lib/docsPaths";
import { ModalPortal } from "@/components/ModalPortal";

interface AutopilotValidationSectionProps {
  validateAutopilotDevice: boolean;
  setValidateAutopilotDevice: (value: boolean) => void;
  validateCorporateIdentifier: boolean;
  setValidateCorporateIdentifier: (value: boolean) => void;
  /** Autopilot device preparation "Device association" — same Graph permission as the two above. */
  validateDeviceAssociation: boolean;
  setValidateDeviceAssociation: (value: boolean) => void;
  /** Intune Enrollment Validation — admits enrolled Intune devices without pre-registration. */
  validateIntuneDeviceBinding?: boolean;
  /** Toggle + persist Intune Enrollment Validation (permission via the Optional Graph capabilities add-on, no consent dialog). */
  onToggleIntuneDeviceBinding?: (v: boolean) => void | Promise<void>;
  /** Whether the IntuneDeviceBinding add-on is granted; null while unknown (loading, transient). */
  intuneEnrollmentPermission?: boolean | null;
  /** Windows 365 Cloud PC validation — fallback gate for Cloud PCs (never Autopilot-registered). */
  validateCloudPcDevice?: boolean;
  /** Toggle + persist Cloud PC validation in one shot (permission comes via the Optional Graph capabilities add-on, no consent dialog). */
  onToggleCloudPc?: (newValue: boolean) => void | Promise<void>;
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
  validateDeviceAssociation,
  setValidateDeviceAssociation,
  validateIntuneDeviceBinding = false,
  onToggleIntuneDeviceBinding,
  intuneEnrollmentPermission = null,
  validateCloudPcDevice = false,
  onToggleCloudPc,
  autopilotConsentInProgress,
  saving,
  onBeginConsent,
  onDetectExistingAccess,
}: AutopilotValidationSectionProps) {
  const enabledValidations = [validateAutopilotDevice, validateCorporateIdentifier, validateDeviceAssociation, validateCloudPcDevice, validateIntuneDeviceBinding];
  const anyValidationEnabled = enabledValidations.some(Boolean);
  const [disableConfirm, setDisableConfirm] = useState<'autopilot' | 'corporate' | 'device-association' | 'cloudpc' | 'intune-enrollment' | null>(null);
  // The three serial-based validations share one Graph permission: the first one enabled
  // carries the admin consent, every further one is a plain persisted toggle.
  const consentAlreadyCarried = validateAutopilotDevice || validateCorporateIdentifier || validateDeviceAssociation;

  const handleToggleAutopilot = () => {
    if (validateAutopilotDevice) {
      setDisableConfirm('autopilot');
    } else if (consentAlreadyCarried) {
      setValidateAutopilotDevice(true);
    } else {
      onBeginConsent('autopilot');
    }
  };

  const handleToggleCorporate = () => {
    if (validateCorporateIdentifier) {
      setDisableConfirm('corporate');
    } else if (consentAlreadyCarried) {
      setValidateCorporateIdentifier(true);
    } else {
      onBeginConsent('corporate');
    }
  };

  const handleToggleDeviceAssociation = () => {
    if (validateDeviceAssociation) {
      setDisableConfirm('device-association');
    } else if (consentAlreadyCarried) {
      setValidateDeviceAssociation(true);
    } else {
      onBeginConsent('device-preparation');
    }
  };

  // Cloud PC validation is a hard gate like the two above, but its permission is an add-on
  // grant (CloudPC.Read.All via the grant script) — no consent dialog, direct toggle+persist.
  const handleToggleCloudPc = () => {
    if (validateCloudPcDevice) {
      setDisableConfirm('cloudpc');
    } else if (onToggleCloudPc) {
      void onToggleCloudPc(true);
    }
  };

  // Intune Enrollment Validation: add-on permission (DeviceManagementManagedDevices.Read.All via
  // the grant script) like Cloud PC — direct toggle+persist, no consent dialog.
  const handleToggleIntuneEnrollment = () => {
    if (validateIntuneDeviceBinding) {
      setDisableConfirm('intune-enrollment');
    } else if (onToggleIntuneDeviceBinding) {
      void onToggleIntuneDeviceBinding(true);
    }
  };

  const confirmDisable = () => {
    if (disableConfirm === 'autopilot') {
      setValidateAutopilotDevice(false);
    } else if (disableConfirm === 'corporate') {
      setValidateCorporateIdentifier(false);
    } else if (disableConfirm === 'device-association') {
      setValidateDeviceAssociation(false);
    } else if (disableConfirm === 'cloudpc' && onToggleCloudPc) {
      void onToggleCloudPc(false);
    } else if (disableConfirm === 'intune-enrollment' && onToggleIntuneDeviceBinding) {
      void onToggleIntuneDeviceBinding(false);
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
      <SectionCardHeader
        tone="amber"
        iconPath="M9 12l2 2 4-4m5.618-4.016A11.955 11.955 0 0112 2.944a11.955 11.955 0 01-8.618 3.04A12.02 12.02 0 003 9c0 5.591 3.824 10.29 9 11.622 5.176-1.332 9-6.03 9-11.622 0-1.042-.133-2.052-.382-3.016z"
        title="Enrollment Device Validation"
        subtitle="Validate devices against Intune registrations before accepting agent data (mandatory for agent ingestion)"
        docsPath={DOCS_PATHS.enrollmentDeviceValidation}
        trailing={
          <span className={`flex-shrink-0 inline-flex items-center px-3 py-1 rounded-full text-xs font-medium ${anyValidationEnabled ? "bg-green-100 text-green-800" : "bg-red-100 text-red-800"}`}>
            {anyValidationEnabled ? "Enabled" : "Disabled"}
          </span>
        }
      />
      <div className="p-6 space-y-5">
        <div className="bg-gray-50 border border-gray-200 rounded-lg p-3 space-y-2">
          <p className="text-sm text-gray-700">
            These validations require the <strong>DeviceManagementServiceConfig.Read.All</strong> permission. Enabling an option starts Microsoft Entra admin consent if not already granted. After consent, the setting is saved automatically. Granting consent requires the <strong>Global Administrator</strong> or <strong>Privileged Role Administrator</strong> Entra role (Application Administrator is not sufficient — Microsoft excludes Graph application permissions from its consent rights).
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
          <label className="flex items-start justify-between gap-4" data-testid="device-association-toggle">
            <div>
              <p className="text-sm font-medium text-gray-900">Enable Device Association Validation</p>
              <p className="text-sm text-gray-500">
                Validates devices against your tenant&apos;s Windows Autopilot{" "}
                <a
                  href="https://learn.microsoft.com/autopilot/device-preparation/device-association/overview"
                  target="_blank"
                  rel="noopener noreferrer"
                  className="underline underline-offset-2 hover:text-gray-700"
                >
                  device association
                </a>{" "}
                list (Intune: Devices → Enrollment → Device association), matched by serial number. Associated
                devices are marked corporate-owned by Intune itself, so no corporate identifier upload is needed.
                Same Graph permission as the two validations above.
              </p>
            </div>
            <button
              onClick={handleToggleDeviceAssociation}
              disabled={saving || autopilotConsentInProgress}
              aria-label="Toggle device association validation"
              className={`relative inline-flex h-6 w-11 shrink-0 items-center rounded-full transition-colors disabled:opacity-60 disabled:cursor-not-allowed ${validateDeviceAssociation ? 'bg-emerald-500' : 'bg-gray-300'}`}
            >
              <span className={`inline-block h-4 w-4 transform rounded-full bg-white transition-transform ${validateDeviceAssociation ? 'translate-x-6' : 'translate-x-1'}`} />
            </button>
          </label>
          {!validateDeviceAssociation && renderDetectButton('device-preparation')}
        </div>

        {/* Windows 365 Cloud PC — fallback gate for Cloud PCs; permission via the Optional Graph capabilities add-on */}
        {onToggleCloudPc && (
          <div className="border-t border-gray-100 pt-5 space-y-3" data-testid="cloudpc-validation-toggle">
            <p className="text-sm font-semibold text-gray-700 tracking-wide">Windows 365</p>
            <label className="flex items-start justify-between gap-4">
              <div>
                <p className="text-sm font-medium text-gray-900">Enable Windows 365 Cloud PC Validation</p>
                <p className="text-sm text-gray-500">
                  Validates Windows 365 Cloud PCs against the tenant&apos;s Cloud PC inventory (Graph{" "}
                  <code className="text-xs">virtualEndpoint/cloudPCs</code>), matched by the Intune device id
                  from the agent&apos;s MDM certificate. Cloud PCs are never Autopilot-registered — enable this
                  as a fallback so first-connect enrollment (Account Setup) of Cloud PCs can be monitored.
                  Requires the optional <strong>CloudPC.Read.All</strong> permission — grant the{" "}
                  <strong>W365CloudPcValidation</strong> add-on under <em>Optional Graph capabilities</em>.
                </p>
              </div>
              <button
                onClick={handleToggleCloudPc}
                disabled={saving}
                aria-label="Toggle Windows 365 Cloud PC validation"
                className={`relative inline-flex h-6 w-11 shrink-0 items-center rounded-full transition-colors disabled:opacity-60 disabled:cursor-not-allowed ${validateCloudPcDevice ? 'bg-emerald-500' : 'bg-gray-300'}`}
              >
                <span className={`inline-block h-4 w-4 transform rounded-full bg-white transition-transform ${validateCloudPcDevice ? 'translate-x-6' : 'translate-x-1'}`} />
              </button>
            </label>
          </div>
        )}

        {/* Intune Enrollment — no pre-registration; permission via the Optional Graph capabilities add-on */}
        {onToggleIntuneDeviceBinding && (
          <div className="border-t border-gray-100 pt-5 space-y-3" data-testid="intune-enrollment-validation-toggle">
            <p className="text-sm font-semibold text-gray-700 tracking-wide">Without pre-registration</p>
            <label className="flex items-start justify-between gap-4">
              <div>
                <p className="text-sm font-medium text-gray-900">Enable Intune Enrollment Validation</p>
                <p className="text-sm text-gray-500">
                  Accepts every device enrolled in this tenant&apos;s Intune, matched by the Intune device id in the
                  agent&apos;s MDM certificate. No Autopilot registration, corporate identifier or device association is
                  needed, which suits Autopilot device preparation without pre-registration. Checked last, only when no
                  option above matched.
                </p>
                <p className="text-sm text-gray-500 mt-1">
                  This reaches exactly as far as your Intune enrollment restrictions: if personal devices may enroll,
                  they are accepted too. For corporate devices only, block personal enrollment or prefer the options above.
                  Requires the optional <strong>DeviceManagementManagedDevices.Read.All</strong>{" "}permission — grant the{" "}
                  <strong>IntuneDeviceBinding</strong>{" "}add-on under{" "}
                  <Link href="/settings/tenant/graph-permissions" className="underline underline-offset-2 hover:text-gray-700">
                    Optional Graph capabilities
                  </Link>.
                </p>
                {intuneEnrollmentPermission === true && (
                  <p className="text-xs font-medium text-emerald-700 mt-1">Permission granted.</p>
                )}
                {intuneEnrollmentPermission === false && (
                  <p className="text-xs font-medium text-amber-700 mt-1">
                    Permission not granted yet. No device is accepted this way until the add-on is granted.
                  </p>
                )}
              </div>
              <button
                onClick={handleToggleIntuneEnrollment}
                disabled={saving}
                aria-label="Toggle Intune enrollment validation"
                className={`relative inline-flex h-6 w-11 shrink-0 items-center rounded-full transition-colors disabled:opacity-60 disabled:cursor-not-allowed ${validateIntuneDeviceBinding ? 'bg-emerald-500' : 'bg-gray-300'}`}
              >
                <span className={`inline-block h-4 w-4 transform rounded-full bg-white transition-transform ${validateIntuneDeviceBinding ? 'translate-x-6' : 'translate-x-1'}`} />
              </button>
            </label>
          </div>
        )}

        <div className="bg-amber-50 border border-amber-200 rounded-lg p-3">
          <p className="text-sm text-amber-900">
            <strong>Important:</strong>{" "}
            If all validations are disabled, backend agent endpoints reject requests for this tenant. Enable at least one and complete admin consent first.
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
        <ModalPortal>
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
                    {disableConfirm === 'autopilot' ? 'Autopilot Device Validation'
                      : disableConfirm === 'corporate' ? 'Corporate Identifier Validation'
                        : disableConfirm === 'device-association' ? 'Device Association Validation'
                          : disableConfirm === 'intune-enrollment' ? 'Intune Enrollment Validation'
                            : 'Windows 365 Cloud PC Validation'}
                  </p>
                </div>
              </div>

              <p className="text-sm text-gray-700 mb-2">
                Are you sure you want to disable this validation?
              </p>
              {enabledValidations.filter(Boolean).length === 1 && (
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
        </ModalPortal>
      )}
    </div>
  );
}
