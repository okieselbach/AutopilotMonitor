"use client";

interface OobeConfigBit {
  bit: number;
  hex: string;
  value: number;
  name: string;
  description: string;
  confidence: "confirmed" | "likely" | "speculated";
  source: string;
}

const OOBE_CONFIG_BITS: OobeConfigBit[] = [
  { bit: 0, hex: "0x0001", value: 1, name: "SkipCortanaOptIn", description: "Skip Cortana opt-in during OOBE", confidence: "confirmed", source: "Microsoft Docs" },
  { bit: 1, hex: "0x0002", value: 2, name: "OobeUserNotLocalAdmin", description: "User is standard user (not local admin)", confidence: "confirmed", source: "Microsoft Docs" },
  { bit: 2, hex: "0x0004", value: 4, name: "SkipExpressSettings", description: "Skip Privacy/Express settings page", confidence: "confirmed", source: "Microsoft Docs" },
  { bit: 3, hex: "0x0008", value: 8, name: "SkipOemRegistration", description: "Skip OEM registration page", confidence: "confirmed", source: "Microsoft Docs" },
  { bit: 4, hex: "0x0010", value: 16, name: "SkipEula", description: "Skip EULA acceptance page", confidence: "confirmed", source: "Microsoft Docs" },
  { bit: 5, hex: "0x0020", value: 32, name: "TPM Attestation", description: "Enable TPM attestation (Self-Deploying)", confidence: "likely", source: "WindowsAutoPilotIntune / Niehaus" },
  { bit: 6, hex: "0x0040", value: 64, name: "AAD Device Auth", description: "AAD device authentication (Self-Deploying)", confidence: "likely", source: "WindowsAutoPilotIntune / Niehaus" },
  { bit: 7, hex: "0x0080", value: 128, name: "AAD TPM Required", description: "Require TPM for AAD join (Self-Deploying)", confidence: "likely", source: "WindowsAutoPilotIntune / Niehaus" },
  { bit: 8, hex: "0x0100", value: 256, name: "SkipWindowsUpgradeUX", description: "Suppress Windows feature update check during OOBE", confidence: "confirmed", source: "WindowsAutoPilotIntune" },
  { bit: 9, hex: "0x0200", value: 512, name: "EnablePatchDownload", description: "Enable quality update download during OOBE", confidence: "likely", source: "Convert-WindowsAutopilotProfile / Niehaus" },
  { bit: 10, hex: "0x0400", value: 1024, name: "SkipKeyboardSelection", description: "Skip keyboard/input method selection page", confidence: "confirmed", source: "WindowsAutoPilotIntune" },
];

const CONFIDENCE_CONFIG = {
  confirmed: { label: "Confirmed", bg: "bg-green-100", text: "text-green-800", dot: "bg-green-500" },
  likely: { label: "Likely", bg: "bg-yellow-100", text: "text-yellow-800", dot: "bg-yellow-500" },
  speculated: { label: "Speculated", bg: "bg-orange-100", text: "text-orange-800", dot: "bg-orange-500" },
};

interface OobeConfigModalProps {
  show: boolean;
  value: number;
  onClose: () => void;
}

export default function OobeConfigModal({ show, value, onClose }: OobeConfigModalProps) {
  if (!show) return null;

  const hexValue = `0x${value.toString(16).toUpperCase().padStart(4, "0")}`;
  const binaryValue = value.toString(2).padStart(11, "0");

  const isSelfDeploying = (value & 0x0020) !== 0 && (value & 0x0040) !== 0;
  const profileType = isSelfDeploying ? "Self-Deploying" : "User-Driven";

  return (
    <div className="fixed inset-0 bg-black bg-opacity-50 flex items-center justify-center z-50" onClick={onClose}>
      <div className="bg-white rounded-lg shadow-xl max-w-lg w-full mx-4 max-h-[90vh] overflow-y-auto" onClick={(e) => e.stopPropagation()}>
        <div className="p-6">
          {/* Header */}
          <div className="flex items-center justify-between mb-4">
            <div className="flex items-center gap-3">
              <div className="flex-shrink-0 w-10 h-10 bg-blue-100 rounded-full flex items-center justify-center">
                <svg className="w-5 h-5 text-blue-600" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M10.325 4.317c.426-1.756 2.924-1.756 3.35 0a1.724 1.724 0 002.573 1.066c1.543-.94 3.31.826 2.37 2.37a1.724 1.724 0 001.066 2.573c1.756.426 1.756 2.924 0 3.35a1.724 1.724 0 00-1.066 2.573c.94 1.543-.826 3.31-2.37 2.37a1.724 1.724 0 00-2.573 1.066c-.426 1.756-2.924 1.756-3.35 0a1.724 1.724 0 00-2.573-1.066c-1.543.94-3.31-.826-2.37-2.37a1.724 1.724 0 00-1.066-2.573c-1.756-.426-1.756-2.924 0-3.35a1.724 1.724 0 001.066-2.573c-.94-1.543.826-3.31 2.37-2.37.996.608 2.296.07 2.572-1.065z" />
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M15 12a3 3 0 11-6 0 3 3 0 016 0z" />
                </svg>
              </div>
              <div>
                <h3 className="text-lg font-semibold text-gray-900">OOBE Config Breakdown</h3>
                <p className="text-sm text-gray-500">
                  {value} ({hexValue}) &middot; {profileType}
                </p>
              </div>
            </div>
            <button onClick={onClose} className="text-gray-400 hover:text-gray-600 transition-colors">
              <svg className="w-5 h-5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
              </svg>
            </button>
          </div>

          {/* Legend */}
          <div className="flex flex-wrap gap-3 mb-4 text-xs">
            {(Object.entries(CONFIDENCE_CONFIG) as [keyof typeof CONFIDENCE_CONFIG, typeof CONFIDENCE_CONFIG[keyof typeof CONFIDENCE_CONFIG]][]).map(([key, conf]) => (
              <span key={key} className={`inline-flex items-center gap-1 px-2 py-0.5 rounded-full ${conf.bg} ${conf.text}`}>
                <span className={`w-1.5 h-1.5 rounded-full ${conf.dot}`} />
                {conf.label}
              </span>
            ))}
          </div>

          {/* Flag list */}
          <div className="space-y-1.5">
            {OOBE_CONFIG_BITS.map((bit) => {
              const isSet = (value & bit.value) !== 0;
              const conf = CONFIDENCE_CONFIG[bit.confidence];
              return (
                <div key={bit.bit} className={`flex items-start gap-2.5 px-3 py-2 rounded-lg ${isSet ? "bg-blue-50 border border-blue-200" : "bg-gray-50 border border-transparent"}`}>
                  <div className="flex-shrink-0 mt-0.5">
                    {isSet ? (
                      <svg className="w-4 h-4 text-blue-600" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                        <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2.5} d="M5 13l4 4L19 7" />
                      </svg>
                    ) : (
                      <span className="w-4 h-4 block text-center text-gray-300 leading-4">&mdash;</span>
                    )}
                  </div>
                  <div className="flex-1 min-w-0">
                    <div className="flex items-center gap-2 flex-wrap">
                      <span className={`font-medium text-sm ${isSet ? "text-gray-900" : "text-gray-400"}`}>{bit.name}</span>
                      <span className="text-[10px] text-gray-400 font-mono">{bit.hex}</span>
                      <span className={`inline-flex items-center gap-1 px-1.5 py-0.5 rounded text-[10px] ${conf.bg} ${conf.text}`}>
                        <span className={`w-1 h-1 rounded-full ${conf.dot}`} />
                        {bit.source}
                      </span>
                    </div>
                    <p className={`text-xs mt-0.5 ${isSet ? "text-gray-600" : "text-gray-400"}`}>{bit.description}</p>
                  </div>
                </div>
              );
            })}
          </div>

          {/* Unknown bits warning */}
          {(() => {
            const knownMask = OOBE_CONFIG_BITS.reduce((acc, b) => acc | b.value, 0);
            const unknownBits = value & ~knownMask;
            if (unknownBits === 0) return null;
            return (
              <div className="mt-3 p-2 bg-amber-50 border border-amber-200 rounded text-xs text-amber-800">
                Unknown bits set: {unknownBits} (0x{unknownBits.toString(16).toUpperCase()}) — these flags are not yet documented.
              </div>
            );
          })()}

          {/* Footer */}
          <div className="mt-4 flex items-center justify-between">
            <p className="text-[10px] text-gray-400 leading-tight max-w-[280px]">
              Sources: Microsoft Docs, WindowsAutoPilotIntune (Niehaus), Convert-WindowsAutopilotProfile.
              Confidence reflects how well each flag is documented.
            </p>
            <button onClick={onClose} className="px-4 py-2 bg-gray-200 text-gray-700 rounded-md hover:bg-gray-300 transition-colors text-sm">
              Close
            </button>
          </div>
        </div>
      </div>
    </div>
  );
}
