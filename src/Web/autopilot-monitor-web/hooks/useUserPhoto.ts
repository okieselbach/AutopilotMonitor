"use client";

import { useEffect, useState } from "react";
import { useMsal } from "@azure/msal-react";
import { graphRequest } from "@/lib/msalConfig";
import { loadUserPhoto } from "@/lib/userPhoto";

/**
 * The signed-in user's profile photo as a data: URL, or null (no photo, not loaded yet,
 * or any failure) — callers render initials for null.
 *
 * Token acquisition is strictly silent: unlike the API token, a missing Graph token must
 * never trigger an interactive redirect for a decorative image.
 */
export function useUserPhoto(): string | null {
  const { instance, accounts } = useMsal();
  const [photo, setPhoto] = useState<{ accountId: string; url: string | null } | null>(null);

  // Depend on the id, not the account object — MSAL hands out a fresh object on every refresh.
  const accountId = accounts[0]?.homeAccountId;

  useEffect(() => {
    if (!accountId) return;
    let cancelled = false;
    const run = async () => {
      const url = await loadUserPhoto(accountId, async () => {
        const account = instance.getAccount({ homeAccountId: accountId });
        if (!account) throw new Error("account gone");
        return (await instance.acquireTokenSilent({ scopes: graphRequest.scopes, account })).accessToken;
      });
      if (!cancelled) setPhoto({ accountId, url });
    };
    void run();
    return () => { cancelled = true; };
  }, [instance, accountId]);

  // Keyed by account so a switched account never shows the previous user's photo.
  return photo && photo.accountId === accountId ? photo.url : null;
}
