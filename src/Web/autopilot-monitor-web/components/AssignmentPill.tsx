// Assignment pill for app and download rows: the management extension's own targeting of the
// policy (`targeted` on the event). Only user-assigned rows get one — device assignment is the
// default and labelling every ordinary row would be noise. Neutral outline like the source and
// Uninstall pills, so it never competes with the coloured state badge next to it.
export default function AssignmentPill({ targeted }: { targeted?: string }) {
  if (targeted !== "User") return null;
  return (
    <span
      className="text-xs px-2 py-0.5 rounded-full bg-gray-100 border border-gray-300 text-gray-600 font-medium whitespace-nowrap"
      title="Assigned to the user in Intune, not to the device — the management extension processes it once the user is signed in."
    >
      User
    </span>
  );
}
