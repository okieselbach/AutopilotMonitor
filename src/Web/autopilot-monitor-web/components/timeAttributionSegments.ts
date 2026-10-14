/** Time-attribution segments in display order, with label and bar colour (session lane and fleet panel). */
export const TIME_ATTRIBUTION_SEGMENTS: { key: string; label: string; color: string }[] = [
  { key: "device_prep", label: "Device preparation", color: "bg-slate-400" },
  { key: "esp_apps", label: "Apps (ESP)", color: "bg-blue-500" },
  { key: "os_update", label: "Windows Update", color: "bg-cyan-500" },
  { key: "awaiting_sign_in", label: "Waiting for sign-in", color: "bg-sky-200" },
  { key: "identity_hello", label: "Identity & Hello", color: "bg-violet-500" },
  { key: "user_esp", label: "User ESP", color: "bg-indigo-400" },
  { key: "desktop_handoff", label: "Desktop handoff", color: "bg-emerald-500" },
  { key: "unattributed", label: "Unattributed", color: "bg-gray-300" },
];
