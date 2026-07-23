"use client";

import { useAuth } from "../../contexts/AuthContext";
import { useRouter } from "next/navigation";
import { useEffect, useState } from "react";
import { api } from "@/lib/api";
import { DOCS_URL } from "@/utils/config";

export default function PreviewPage() {
  const { isAuthenticated, isLoading, user, isPreviewBlocked, previewMessage, logout, getAccessToken } = useAuth();
  const router = useRouter();

  const [notificationEmail, setNotificationEmail] = useState("");
  const [emailStatus, setEmailStatus] = useState<"idle" | "saving" | "saved" | "error">("idle");
  const [emailError, setEmailError] = useState("");

  // If not preview-blocked (e.g. approved tenant navigates here), redirect away
  useEffect(() => {
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
  }, [isAuthenticated, isLoading, user, isPreviewBlocked, router]);

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

  if (isLoading) {
    return (
      <div className="landing-page min-h-screen bg-gradient-to-br from-blue-50 via-indigo-50 to-purple-50 flex items-center justify-center">
        <div className="text-center">
          <div className="animate-spin rounded-full h-12 w-12 border-b-2 border-blue-600 mx-auto"></div>
          <p className="mt-4 text-gray-600">Loading...</p>
        </div>
      </div>
    );
  }

  return (
    <div className="landing-page min-h-screen bg-gradient-to-br from-blue-50 via-indigo-50 to-purple-50 flex items-center justify-center px-6">
      <div className="max-w-lg w-full text-center">
        {/* Logo */}
        <div className="flex items-center justify-center space-x-3 mb-8">
          <div className="w-12 h-12 bg-gradient-to-br from-blue-600 to-indigo-600 rounded-lg flex items-center justify-center">
            <svg className="w-7 h-7 text-white" fill="none" viewBox="0 0 24 24" stroke="currentColor">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 12l2 2 4-4m6 2a9 9 0 11-18 0 9 9 0 0118 0z" />
            </svg>
          </div>
          <span className="text-2xl font-bold bg-gradient-to-r from-blue-600 to-indigo-600 bg-clip-text text-transparent">
            Autopilot Monitor
          </span>
        </div>

        {/* Card */}
        <div className="bg-white rounded-2xl shadow-xl p-10">
          {/* Clock icon */}
          <div className="w-16 h-16 bg-amber-100 rounded-full flex items-center justify-center mx-auto mb-6">
            <svg className="w-8 h-8 text-amber-600" fill="none" viewBox="0 0 24 24" stroke="currentColor">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 8v4l3 3m6-3a9 9 0 11-18 0 9 9 0 0118 0z" />
            </svg>
          </div>

          <h1 className="text-2xl font-normal text-gray-900 mb-3">
            Private Preview
          </h1>

          <p className="text-gray-600 mb-6 leading-relaxed">
            {previewMessage || "Autopilot Monitor is currently in private preview. Your organization has been added to the waitlist."}
          </p>

          <div className="bg-blue-50 rounded-lg p-4 mb-6">
            <p className="text-sm text-blue-700">
              Signed in as <span className="font-semibold">{user?.upn}</span>
            </p>
            <p className="text-xs text-blue-500 mt-1">
              Tenant: {user?.tenantId}
            </p>
          </div>

          {/* Notification Email */}
          <div className="text-left bg-indigo-50 border border-indigo-200 rounded-lg p-4 mb-6">
            <p className="text-sm font-semibold text-indigo-900 mb-1">Get notified when approved</p>
            <p className="text-sm text-indigo-700 mb-3">
              Enter your email and we'll notify you as soon as your Private Preview access is granted.
            </p>
            <div className="flex gap-2">
              <input
                type="email"
                value={notificationEmail}
                onChange={(e) => { setNotificationEmail(e.target.value); setEmailStatus("idle"); setEmailError(""); }}
                placeholder="your@email.com"
                className="flex-1 px-3 py-2 border border-indigo-300 rounded-lg text-sm text-gray-900 placeholder-gray-400 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 transition-colors"
                onKeyDown={(e) => { if (e.key === "Enter") handleSaveEmail(); }}
              />
              <button
                onClick={handleSaveEmail}
                disabled={emailStatus === "saving" || !notificationEmail.trim()}
                className="px-4 py-2 text-sm font-medium text-white bg-indigo-600 rounded-lg hover:bg-indigo-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors whitespace-nowrap"
              >
                {emailStatus === "saving" ? "Saving..." : "Notify me"}
              </button>
            </div>
            {emailStatus === "saved" && (
              <p className="text-xs text-green-600 mt-2 font-medium">
                Email saved! We'll send you a notification when your access is approved.
              </p>
            )}
            {emailStatus === "error" && emailError && (
              <p className="text-xs text-red-600 mt-2 font-medium">{emailError}</p>
            )}
          </div>

          <div className="text-left bg-amber-50 border border-amber-200 rounded-lg p-4 mb-6">
            <p className="text-sm font-semibold text-amber-900 mb-1">Next steps</p>
            <p className="text-sm text-amber-800">
              Please sign out and contact me on LinkedIn or open a GitHub issue to request access to the Private Preview. I check incoming requests regularly and will approve them as quickly as possible if I have enough capacity left.
            </p>
            <p className="text-sm text-amber-800 mt-2">
              In the meantime, you can already review the setup and configuration in the <a href={DOCS_URL} target="_blank" rel="noopener noreferrer" className="underline hover:no-underline">documentation</a>.
            </p>
            <p className="text-sm text-amber-800 mt-2">
              When you signed-up, sign in again later to view the updated approval status on your dashboard.
            </p>
            <div className="flex flex-wrap gap-2 mt-3">
              <a
                href="https://www.linkedin.com/in/oliver-kieselbach/"
                target="_blank"
                rel="noopener noreferrer"
                className="inline-flex items-center gap-1.5 px-3 py-1.5 text-sm font-medium text-blue-700 bg-blue-100 rounded-md hover:bg-blue-200 transition-colors"
              >
                LinkedIn
                <svg className="w-3.5 h-3.5" fill="none" viewBox="0 0 24 24" strokeWidth={2} stroke="currentColor">
                  <path strokeLinecap="round" strokeLinejoin="round" d="M13.5 6H5.25A2.25 2.25 0 003 8.25v10.5A2.25 2.25 0 005.25 21h10.5A2.25 2.25 0 0018 18.75V10.5m-10.5 6L21 3m0 0h-5.25M21 3v5.25" />
                </svg>
              </a>
              <a
                href="https://github.com/okieselbach/Autopilot-Monitor/issues"
                target="_blank"
                rel="noopener noreferrer"
                className="inline-flex items-center gap-1.5 px-3 py-1.5 text-sm font-medium text-blue-700 bg-blue-100 rounded-md hover:bg-blue-200 transition-colors"
              >
                GitHub Issues
                <svg className="w-3.5 h-3.5" fill="none" viewBox="0 0 24 24" strokeWidth={2} stroke="currentColor">
                  <path strokeLinecap="round" strokeLinejoin="round" d="M13.5 6H5.25A2.25 2.25 0 003 8.25v10.5A2.25 2.25 0 005.25 21h10.5A2.25 2.25 0 0018 18.75V10.5m-10.5 6L21 3m0 0h-5.25M21 3v5.25" />
                </svg>
              </a>
            </div>
          </div>

          <button
            onClick={logout}
            className="px-6 py-3 bg-gray-100 text-gray-700 rounded-lg font-semibold hover:bg-gray-200 transition-colors"
          >
            Sign Out
          </button>
        </div>

        <p className="mt-6 text-sm text-gray-400">
          &copy; 2026 Autopilot Monitor. Powered by Azure and Microsoft Identity.
        </p>
      </div>
    </div>
  );
}
