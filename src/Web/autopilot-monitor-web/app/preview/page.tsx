"use client";

import { useAuth } from "../../contexts/AuthContext";
import { useRouter } from "next/navigation";
import { useEffect, useState } from "react";
import { api } from "@/lib/api";
import { DOCS_URL } from "@/utils/config";
import { BrandMark } from "../../components/BrandMark";

export default function PreviewPage() {
  const { isAuthenticated, isLoading, user, isPreviewBlocked, previewMessage, logout, getAccessToken } = useAuth();
  const router = useRouter();

  const [notificationEmail, setNotificationEmail] = useState("");
  const [emailStatus, setEmailStatus] = useState<"idle" | "saving" | "saved" | "error">("idle");
  const [emailError, setEmailError] = useState("");

  // ?demo=1 renders the page without auth/redirects (placeholder data) so
  // the design can be reviewed locally and on SWA preview environments.
  const [demo, setDemo] = useState<boolean | null>(null);
  useEffect(() => {
    setDemo(new URLSearchParams(window.location.search).has("demo"));
  }, []);

  // If not preview-blocked (e.g. approved tenant navigates here), redirect away
  useEffect(() => {
    if (demo === null || demo) return;
    if (!isLoading && isAuthenticated && user && !isPreviewBlocked) {
      if (user.isTenantAdmin || user.isGlobalAdmin) {
        router.push("/dashboard");
      } else {
        router.push("/progress");
      }
    }
    if (!isLoading && !isAuthenticated) {
      router.push("/");
    }
  }, [demo, isAuthenticated, isLoading, user, isPreviewBlocked, router]);

  const handleSaveEmail = async () => {
    const email = notificationEmail.trim();
    if (!email || !email.includes("@")) {
      setEmailError("Please enter a valid email address.");
      setEmailStatus("error");
      return;
    }

    try {
      setEmailStatus("saving");
      setEmailError("");

      const token = await getAccessToken();
      if (!token) {
        throw new Error("Not authenticated");
      }

      const response = await fetch(api.preview.notificationEmail(), {
        method: "PUT",
        headers: {
          "Authorization": `Bearer ${token}`,
          "Content-Type": "application/json",
        },
        body: JSON.stringify({ email }),
      });

      if (!response.ok) {
        const data = await response.json().catch(() => ({}));
        throw new Error(data.error || "Failed to save email");
      }

      setEmailStatus("saved");
      setTimeout(() => setEmailStatus("idle"), 5000);
    } catch (err) {
      setEmailError(err instanceof Error ? err.message : "Failed to save email");
      setEmailStatus("error");
    }
  };

  if (!demo && isLoading) {
    return (
      <div className="landing-v2 min-h-screen bg-[var(--lp-bg)] flex items-center justify-center">
        <div className="text-center">
          <div className="animate-spin rounded-full h-12 w-12 border-b-2 border-[var(--lp-accent)] mx-auto"></div>
          <p className="mt-4 text-[var(--lp-ink-soft)]">Loading...</p>
        </div>
      </div>
    );
  }

  const upn = user?.upn ?? "you@contoso.com";
  const tenantId = user?.tenantId ?? "00000000-0000-0000-0000-000000000000";

  return (
    <div className="landing-v2 min-h-screen bg-[var(--lp-bg)] flex items-center justify-center px-6 py-12">
      <div className="max-w-lg w-full text-center">
        {/* Brand */}
        <div className="flex items-center justify-center gap-2.5 mb-8">
          <BrandMark className="w-7 h-7" />
          <span className="text-xl font-bold tracking-tight text-[var(--lp-ink)]">Autopilot Monitor</span>
        </div>

        {/* Card */}
        <div className="bg-[var(--lp-surface)] border border-[var(--lp-line)] rounded-2xl shadow-xl shadow-black/[0.06] p-8 sm:p-10">
          <div className="w-14 h-14 bg-[var(--lp-warn-soft)] rounded-full flex items-center justify-center mx-auto mb-5">
            <svg className="w-7 h-7 text-[var(--lp-warn)]" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
              <path strokeLinecap="round" strokeLinejoin="round" d="M12 8v4l3 3m6-3a9 9 0 11-18 0 9 9 0 0118 0z" />
            </svg>
          </div>

          <h1 className="text-2xl font-bold tracking-tight text-[var(--lp-ink)] mb-2">
            Almost there — you&apos;re on the list
          </h1>

          <p className="text-[15px] text-[var(--lp-ink-soft)] mb-6 leading-relaxed">
            {previewMessage || "Autopilot Monitor is currently in preview. Your organization has been added to the waitlist."}
          </p>

          <div className="bg-[var(--lp-surface-2)] border border-[var(--lp-line-soft)] rounded-xl p-3.5 mb-5">
            <p className="text-sm text-[var(--lp-ink-soft)]">
              Signed in as <span className="font-semibold text-[var(--lp-ink)]">{upn}</span>
            </p>
            <p className="text-xs text-[var(--lp-ink-faint)] mt-0.5 font-mono">Tenant: {tenantId}</p>
          </div>

          {/* Notification Email */}
          <div className="text-left bg-[var(--lp-accent-soft)] border border-[var(--lp-accent-line)] rounded-xl p-4 mb-5">
            <p className="text-sm font-semibold text-[var(--lp-ink)] mb-1">Get notified when approved</p>
            <p className="text-sm text-[var(--lp-ink-soft)] mb-3">
              Enter your email and we&apos;ll notify you as soon as your preview access is granted.
            </p>
            <div className="flex gap-2">
              <input
                type="email"
                value={notificationEmail}
                onChange={(e) => { setNotificationEmail(e.target.value); setEmailStatus("idle"); setEmailError(""); }}
                placeholder="your@email.com"
                className="flex-1 px-3 py-2 border border-[var(--lp-line)] bg-[var(--lp-surface)] rounded-lg text-sm text-[var(--lp-ink)] placeholder-[var(--lp-ink-faint)] focus:outline-none focus:ring-2 focus:ring-[var(--lp-accent)] focus:border-[var(--lp-accent)] transition-colors"
                onKeyDown={(e) => { if (e.key === "Enter") handleSaveEmail(); }}
              />
              <button
                onClick={handleSaveEmail}
                disabled={emailStatus === "saving" || !notificationEmail.trim()}
                className="px-4 py-2 text-sm font-semibold text-white bg-[var(--lp-accent-ink)] rounded-lg hover:brightness-110 disabled:opacity-50 disabled:cursor-not-allowed transition-all whitespace-nowrap"
              >
                {emailStatus === "saving" ? "Saving..." : "Notify me"}
              </button>
            </div>
            {emailStatus === "saved" && (
              <p className="text-xs text-[var(--lp-accent-ink)] mt-2 font-medium">
                Email saved! We&apos;ll send you a notification when your access is approved.
              </p>
            )}
            {emailStatus === "error" && emailError && (
              <p className="text-xs text-[var(--lp-danger)] mt-2 font-medium">{emailError}</p>
            )}
          </div>

          <div className="text-left bg-[var(--lp-surface-2)] border border-[var(--lp-line-soft)] rounded-xl p-4 mb-6">
            <p className="text-sm font-semibold text-[var(--lp-ink)] mb-1">Next steps</p>
            <p className="text-sm text-[var(--lp-ink-soft)]">
              Please sign out and contact me on LinkedIn or open a GitHub issue to request access to the
              preview. I check incoming requests regularly and will approve them as quickly as possible
              if I have enough capacity left.
            </p>
            <p className="text-sm text-[var(--lp-ink-soft)] mt-2">
              In the meantime, you can already review the setup and configuration in the{" "}
              <a href={DOCS_URL} target="_blank" rel="noopener noreferrer" className="text-[var(--lp-accent-ink)] hover:opacity-80 underline">documentation</a>.
            </p>
            <p className="text-sm text-[var(--lp-ink-soft)] mt-2">
              When you signed-up, sign in again later to view the updated approval status on your dashboard.
            </p>
            <div className="flex flex-wrap gap-2 mt-3">
              {[
                { label: "LinkedIn", href: "https://www.linkedin.com/in/oliver-kieselbach/" },
                { label: "GitHub Issues", href: "https://github.com/okieselbach/AutopilotMonitor/issues" },
              ].map(link => (
                <a
                  key={link.label}
                  href={link.href}
                  target="_blank"
                  rel="noopener noreferrer"
                  className="inline-flex items-center gap-1.5 px-3 py-1.5 text-sm font-medium text-[var(--lp-ink)] border border-[var(--lp-line)] bg-[var(--lp-surface)] rounded-lg hover:border-[var(--lp-ink-faint)] transition-colors"
                >
                  {link.label}
                  <svg className="w-3.5 h-3.5 text-[var(--lp-ink-faint)]" fill="none" viewBox="0 0 24 24" strokeWidth={2} stroke="currentColor">
                    <path strokeLinecap="round" strokeLinejoin="round" d="M13.5 6H5.25A2.25 2.25 0 003 8.25v10.5A2.25 2.25 0 005.25 21h10.5A2.25 2.25 0 0018 18.75V10.5m-10.5 6L21 3m0 0h-5.25M21 3v5.25" />
                  </svg>
                </a>
              ))}
            </div>
          </div>

          <button
            onClick={logout}
            className="px-6 py-2.5 border border-[var(--lp-line)] bg-[var(--lp-surface)] text-[var(--lp-ink)] rounded-lg font-semibold hover:border-[var(--lp-ink-faint)] transition-colors"
          >
            Sign Out
          </button>
        </div>

        <p className="mt-6 text-sm text-[var(--lp-ink-faint)]">
          &copy; 2026 Autopilot Monitor. Powered by Azure and Microsoft Identity.
        </p>
      </div>
    </div>
  );
}
