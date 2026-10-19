/** A metrics tile: label, figure, one line of context. Shared by the GA metrics sections. */
export function Stat({ label, value, hint }: { label: string; value: string; hint: string }) {
  return (
    <div className="bg-white shadow rounded-lg p-4">
      <p className="text-xs font-medium text-gray-500 uppercase tracking-wider">{label}</p>
      <p className="mt-1 text-2xl font-semibold text-gray-900">{value}</p>
      <p className="text-xs text-gray-400">{hint}</p>
    </div>
  );
}
