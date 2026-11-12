// Page-wide key handlers stay out of the way while the user types: a key pressed in a text field
// belongs to that field, not to a shortcut.
export function isTypingInField(target: EventTarget | null): boolean {
  if (!(target instanceof HTMLElement)) return false;
  return target.isContentEditable || target.tagName === "INPUT" || target.tagName === "TEXTAREA" || target.tagName === "SELECT";
}
