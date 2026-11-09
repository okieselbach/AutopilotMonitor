/**
 * The data-boundary disclosure every opt-in path into hosted diagnostics upload shows: Collect
 * Logs on a session and the setup guide (Settings → Diagnostics Package has its own, longer block).
 * The privacy page and the trust FAQ promise that hosted upload is only ever enabled behind a
 * clearly marked "data leaves your tenant" disclosure — this block is that promise.
 */
export function HostedUploadDisclosure() {
  return (
    <div className="bg-sky-50 border border-sky-200 rounded-lg p-3 space-y-1 dark:bg-sky-900/20 dark:border-sky-800">
      <p className="text-sm text-sky-900 dark:text-sky-100">
        <span className="font-semibold">Uploaded packages leave your own Azure tenant boundary.</span>{" "}
        They are stored in the Autopilot Monitor backend&apos;s Azure Storage (Germany West Central),
        isolated per tenant, and each upload uses a fresh blob-scoped, write-only token.
      </p>
      <p className="text-xs text-sky-800 dark:text-sky-200">
        Packages are removed automatically according to your <span className="font-semibold">Data Retention Days</span> setting.
      </p>
    </div>
  );
}
