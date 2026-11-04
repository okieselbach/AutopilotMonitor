/** Marks a Fleet Health section the group-tag view filter does not reach: its data carries no group tag. */
export default function NotFilteredByGroupTag() {
  return (
    <span
      className="ml-2 text-xs font-normal text-amber-700"
      title="This section is computed from data that carries no group tag, so the group tag filter above does not apply to it."
    >
      not filtered by group tag
    </span>
  );
}
