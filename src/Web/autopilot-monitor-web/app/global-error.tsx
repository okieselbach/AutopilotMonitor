"use client";

/**
 * Root-level error boundary — catches unhandled client-side exceptions that
 * escape every other boundary (including layout-level crashes such as MSAL
 * failures on mobile tab-resume).
 *
 * Next.js requires this component to render its own <html>/<body> tags because
 * it replaces the entire root layout when triggered.
 */
export default function GlobalError({
  reset,
}: {
  error: Error & { digest?: string };
  reset: () => void;
}) {
  return (
    <html lang="en">
      <body style={{ margin: 0, fontFamily: "system-ui, -apple-system, sans-serif" }}>
        <div
          style={{
            minHeight: "100vh",
            display: "flex",
            alignItems: "center",
            justifyContent: "center",
            background: "linear-gradient(135deg, #eff6ff 0%, #e0e7ff 100%)",
            padding: "1rem",
          }}
        >
          <div
            style={{
              background: "#fff",
              borderRadius: "0.75rem",
              boxShadow: "0 4px 24px rgba(0,0,0,0.10)",
              padding: "2rem",
              maxWidth: "28rem",
              width: "100%",
              textAlign: "center",
            }}
          >
            <div style={{ fontSize: "2.5rem", marginBottom: "0.75rem" }}>!</div>
            <h2 style={{ fontSize: "1.25rem", fontWeight: 600, color: "#111827", marginBottom: "0.5rem" }}>
              Something went wrong
            </h2>
            <p style={{ color: "#6b7280", marginBottom: "1.5rem", lineHeight: 1.5 }}>
              Your session may have expired or the app encountered an unexpected error.
            </p>
            <div style={{ display: "flex", gap: "0.75rem", justifyContent: "center", flexWrap: "wrap" }}>
              <button
                onClick={() => {
                  // Clear MSAL state from sessionStorage to avoid stale-cache loops
                  try {
                    Object.keys(sessionStorage).forEach((key) => {
                      if (key.startsWith("msal.")) {
                        sessionStorage.removeItem(key);
                      }
                    });
                  } catch {
                    // sessionStorage may be unavailable
                  }
                  window.location.href = "/";
                }}
                style={{
                  padding: "0.625rem 1.25rem",
                  background: "#2563eb",
                  color: "#fff",
                  border: "none",
                  borderRadius: "0.5rem",
                  cursor: "pointer",
                  fontWeight: 500,
                  fontSize: "0.875rem",
                }}
              >
                Sign in again
              </button>
              <button
                onClick={() => reset()}
                style={{
                  padding: "0.625rem 1.25rem",
                  background: "#f3f4f6",
                  color: "#374151",
                  border: "1px solid #d1d5db",
                  borderRadius: "0.5rem",
                  cursor: "pointer",
                  fontWeight: 500,
                  fontSize: "0.875rem",
                }}
              >
                Try again
              </button>
            </div>
          </div>
        </div>
      </body>
    </html>
  );
}
