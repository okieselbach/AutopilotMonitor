/**
 * What the reseed section shows for the answer of a reseed, kept out of the component so it is
 * unit tested.
 */
import type { ReseedFromGitHubResponse, ReseedInventoryNode } from "@/lib/generated/wire-types.generated";

function inventoryLine(inventory: ReseedInventoryNode): string {
  const line = `Inventory rows: ${inventory.set} set, ${inventory.cleared} cleared (${inventory.rows} checked)`;
  return inventory.failed > 0 ? `${line}, ${inventory.failed} failed` : line;
}

/**
 * The notice for a reseed answer, one statement per line. The request can succeed while the
 * answer says the reseed did not (a refused mapping list): that never reads as a success, it
 * shows the backend's message and every problem it names.
 */
export function reseedNotice(
  result: Pick<ReseedFromGitHubResponse, "success" | "message" | "cpeProblems" | "cpeInventory">,
  successText: string,
): { ok: boolean; text: string } {
  const ok = result.success !== false;
  const lines = ok ? [successText] : [result.message || "Reseed from GitHub failed", ...(result.cpeProblems ?? [])];
  if (result.cpeInventory) lines.push(inventoryLine(result.cpeInventory));
  return { ok, text: lines.join("\n") };
}
