"use client";

import { useCallback, useEffect, useState } from "react";
import { api } from "@/lib/api";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";
import { useAdminConfig } from "../../AdminConfigContext";
import { AdminNotifications } from "../../AdminNotifications";

type TemplateKind = "welcome" | "farewell";

interface TemplateState {
  kind: TemplateKind;
  subject: string;
  isOverridden: boolean;
  html: string;
  builtInHtml: string;
  updatedBy: string | null;
  updatedUtc: string | null;
  placeholder: string;
  maxLength: number;
}

const KINDS: { kind: TemplateKind; title: string; description: string }[] = [
  { kind: "welcome", title: "Welcome email", description: "Sent once when a tenant is activated, to the notification address entered on the activation page." },
  { kind: "farewell", title: "Farewell email", description: "Sent once after a tenant's offboarding completes, to the address captured at offboarding start." },
];

const SAMPLE_DOMAIN = "contoso.com";

function renderPreview(html: string, placeholder: string): string {
  return html.split(placeholder).join(SAMPLE_DOMAIN);
}

function formatUtc(value: string | null): string {
  if (!value) return "";
  const d = new Date(value);
  return Number.isNaN(d.getTime()) ? value : d.toISOString().replace("T", " ").substring(0, 16) + " UTC";
}

export function SectionEmailTemplates() {
  const { getAccessToken, setError, setSuccessMessage } = useAdminConfig();

  return (
    <>
      <AdminNotifications />
      <div className="bg-white dark:bg-gray-800 rounded-lg shadow">
        <div className="px-6 py-4 border-b border-gray-200 dark:border-gray-700">
          <h2 className="text-lg font-semibold text-gray-900 dark:text-white">Email Templates</h2>
          <p className="text-sm text-gray-500 dark:text-gray-400 mt-1">
            Preview the transactional emails, send a test to your tenant&apos;s contact address, or replace the
            HTML without a code deployment. Changes apply to real sends immediately; subjects are fixed.
          </p>
        </div>
        <div className="p-6 space-y-6">
          {KINDS.map((k) => (
            <TemplateCard
              key={k.kind}
              kind={k.kind}
              title={k.title}
              description={k.description}
              getAccessToken={getAccessToken}
              setError={setError}
              setSuccessMessage={setSuccessMessage}
            />
          ))}
        </div>
      </div>
    </>
  );
}

interface TemplateCardProps {
  kind: TemplateKind;
  title: string;
  description: string;
  getAccessToken: () => Promise<string | null>;
  setError: (error: string | null) => void;
  setSuccessMessage: (message: string | null) => void;
}

function TemplateCard({ kind, title, description, getAccessToken, setError, setSuccessMessage }: TemplateCardProps) {
  const [state, setState] = useState<TemplateState | null>(null);
  const [loading, setLoading] = useState(true);
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState("");
  const [busy, setBusy] = useState<"save" | "reset" | "test" | null>(null);
  const [previewHtml, setPreviewHtml] = useState<string | null>(null);

  const fail = useCallback((err: unknown, fallback: string) => {
    if (err instanceof TokenExpiredError) {
      console.error("Session expired", err);
    } else {
      console.error(fallback, err);
    }
    setError(err instanceof Error ? err.message : fallback);
  }, [setError]);

  const load = useCallback(async () => {
    try {
      setLoading(true);
      const response = await authenticatedFetch(api.emailTemplates.get(kind), getAccessToken);
      if (!response.ok) throw new Error(`Failed to load ${kind} template: ${response.statusText}`);
      const data = (await response.json()) as TemplateState;
      setState(data);
      setDraft(data.html);
    } catch (err) {
      fail(err, `Failed to load ${kind} template`);
    } finally {
      setLoading(false);
    }
  }, [kind, getAccessToken, fail]);

  useEffect(() => {
    const run = async () => { await load(); };
    void run();
  }, [load]);

  const flash = (message: string) => {
    setSuccessMessage(message);
    setTimeout(() => setSuccessMessage(null), 6000);
  };

  const handleSave = async () => {
    if (!state) return;
    try {
      setBusy("save");
      setError(null);
      const response = await authenticatedFetch(api.emailTemplates.save(kind), getAccessToken, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ html: draft }),
      });
      if (!response.ok) {
        const body = await response.json().catch(() => null);
        throw new Error(body?.error ?? `Failed to save ${kind} template: ${response.statusText}`);
      }
      flash(`${title} saved — real sends use the customized HTML from now on.`);
      setEditing(false);
      await load();
    } catch (err) {
      fail(err, `Failed to save ${kind} template`);
    } finally {
      setBusy(null);
    }
  };

  const handleReset = async () => {
    if (!window.confirm(`Reset the ${title.toLowerCase()} to the built-in template? The customized HTML will be deleted.`)) return;
    try {
      setBusy("reset");
      setError(null);
      const response = await authenticatedFetch(api.emailTemplates.reset(kind), getAccessToken, { method: "DELETE" });
      if (!response.ok) throw new Error(`Failed to reset ${kind} template: ${response.statusText}`);
      flash(`${title} reset to the built-in template.`);
      setEditing(false);
      await load();
    } catch (err) {
      fail(err, `Failed to reset ${kind} template`);
    } finally {
      setBusy(null);
    }
  };

  const handleSendTest = async (useDraft: boolean) => {
    try {
      setBusy("test");
      setError(null);
      const response = await authenticatedFetch(api.emailTemplates.sendTest(kind), getAccessToken, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(useDraft ? { html: draft } : {}),
      });
      const body = await response.json().catch(() => null);
      if (!response.ok) throw new Error(body?.error ?? `Failed to send ${kind} test email: ${response.statusText}`);
      flash(`Test ${title.toLowerCase()} sent to ${body?.sentTo ?? "your tenant contact address"}${useDraft ? " (unsaved draft)" : ""}.`);
    } catch (err) {
      fail(err, `Failed to send ${kind} test email`);
    } finally {
      setBusy(null);
    }
  };

  const draftDirty = state !== null && draft !== state.html;
  const draftTooLong = state !== null && draft.length > state.maxLength;

  return (
    <div className="border border-gray-200 dark:border-gray-700 rounded-lg">
      <div className="px-5 py-4 flex flex-wrap items-start justify-between gap-3">
        <div>
          <div className="flex items-center gap-2">
            <h3 className="text-base font-semibold text-gray-900 dark:text-white">{title}</h3>
            {state && (
              state.isOverridden ? (
                <span className="inline-flex items-center rounded-full border border-gray-300 dark:border-gray-600 px-2 py-0.5 text-xs text-gray-700 dark:text-gray-300">
                  Customized{state.updatedBy ? ` by ${state.updatedBy}` : ""}{state.updatedUtc ? ` · ${formatUtc(state.updatedUtc)}` : ""}
                </span>
              ) : (
                <span className="inline-flex items-center rounded-full border border-gray-300 dark:border-gray-600 px-2 py-0.5 text-xs text-gray-700 dark:text-gray-300">
                  Built-in
                </span>
              )
            )}
          </div>
          <p className="text-sm text-gray-500 dark:text-gray-400 mt-1">{description}</p>
          {state && (
            <p className="text-xs text-gray-500 dark:text-gray-400 mt-1">
              Subject: <span className="font-medium text-gray-700 dark:text-gray-300">{state.subject}</span>
            </p>
          )}
        </div>
        <div className="flex flex-wrap gap-2">
          <button
            type="button"
            disabled={!state || loading}
            onClick={() => state && setPreviewHtml(renderPreview(state.html, state.placeholder))}
            className="px-3 py-1.5 text-sm rounded border border-gray-300 dark:border-gray-600 text-gray-700 dark:text-gray-200 hover:bg-gray-50 dark:hover:bg-gray-700 disabled:opacity-50"
          >
            Preview
          </button>
          <button
            type="button"
            disabled={!state || busy !== null}
            onClick={() => handleSendTest(false)}
            className="px-3 py-1.5 text-sm rounded border border-gray-300 dark:border-gray-600 text-gray-700 dark:text-gray-200 hover:bg-gray-50 dark:hover:bg-gray-700 disabled:opacity-50"
          >
            {busy === "test" ? "Sending…" : "Send test to me"}
          </button>
          <button
            type="button"
            disabled={!state || loading}
            onClick={() => { setEditing((v) => !v); if (state) setDraft(state.html); }}
            className="px-3 py-1.5 text-sm rounded bg-green-600 text-white hover:bg-green-700 disabled:opacity-50"
          >
            {editing ? "Close editor" : "Edit HTML"}
          </button>
        </div>
      </div>

      {editing && state && (
        <div className="px-5 pb-5 space-y-3 border-t border-gray-200 dark:border-gray-700 pt-4">
          <p className="text-xs text-gray-500 dark:text-gray-400">
            Use <code className="font-mono bg-gray-100 dark:bg-gray-700 px-1 rounded">{state.placeholder}</code> where the tenant&apos;s domain
            should appear (an empty domain renders as &quot;your organization&quot;). Inline CSS only — mail clients ignore
            stylesheets, gradients and images may be blocked. Maximum {state.maxLength.toLocaleString()} characters.
          </p>
          {draftTooLong && (
            <p className="text-xs text-red-600 bg-red-50 border border-red-200 rounded px-3 py-2">
              {draft.length.toLocaleString()} characters — exceeds the {state.maxLength.toLocaleString()} character limit.
            </p>
          )}
          <textarea
            value={draft}
            onChange={(e) => setDraft(e.target.value)}
            rows={22}
            spellCheck={false}
            className="w-full px-4 py-3 border border-gray-300 dark:border-gray-600 rounded-lg text-sm font-mono text-gray-900 dark:text-gray-100 bg-gray-50 dark:bg-gray-900 focus:outline-none focus:ring-2 focus:ring-green-500 focus:border-green-500 resize-y"
          />
          <div className="flex flex-wrap items-center justify-between gap-2">
            <div className="flex flex-wrap gap-2">
              <button
                type="button"
                onClick={() => setPreviewHtml(renderPreview(draft, state.placeholder))}
                className="px-3 py-1.5 text-sm rounded border border-gray-300 dark:border-gray-600 text-gray-700 dark:text-gray-200 hover:bg-gray-50 dark:hover:bg-gray-700"
              >
                Preview draft
              </button>
              <button
                type="button"
                disabled={busy !== null || draftTooLong || draft.trim() === ""}
                onClick={() => handleSendTest(true)}
                className="px-3 py-1.5 text-sm rounded border border-gray-300 dark:border-gray-600 text-gray-700 dark:text-gray-200 hover:bg-gray-50 dark:hover:bg-gray-700 disabled:opacity-50"
              >
                {busy === "test" ? "Sending…" : "Send draft as test"}
              </button>
              <button
                type="button"
                disabled={busy !== null}
                onClick={() => setDraft(state.builtInHtml)}
                className="px-3 py-1.5 text-sm rounded border border-gray-300 dark:border-gray-600 text-gray-700 dark:text-gray-200 hover:bg-gray-50 dark:hover:bg-gray-700 disabled:opacity-50"
              >
                Load built-in HTML
              </button>
            </div>
            <div className="flex flex-wrap gap-2">
              {state.isOverridden && (
                <button
                  type="button"
                  disabled={busy !== null}
                  onClick={handleReset}
                  className="px-3 py-1.5 text-sm rounded border border-red-300 text-red-700 hover:bg-red-50 dark:border-red-700 dark:text-red-300 dark:hover:bg-red-900/30 disabled:opacity-50"
                >
                  {busy === "reset" ? "Resetting…" : "Reset to built-in"}
                </button>
              )}
              <button
                type="button"
                disabled={busy !== null || !draftDirty || draftTooLong || draft.trim() === ""}
                onClick={handleSave}
                className="px-3 py-1.5 text-sm rounded bg-green-600 text-white hover:bg-green-700 disabled:opacity-50"
              >
                {busy === "save" ? "Saving…" : "Save"}
              </button>
            </div>
          </div>
        </div>
      )}

      {previewHtml !== null && (
        <PreviewModal title={`${title} · preview (${SAMPLE_DOMAIN})`} html={previewHtml} onClose={() => setPreviewHtml(null)} />
      )}
    </div>
  );
}

function PreviewModal({ title, html, onClose }: { title: string; html: string; onClose: () => void }) {
  return (
    <div
      className="fixed inset-0 z-50 flex items-center justify-center bg-black bg-opacity-50 p-4"
      onClick={onClose}
    >
      <div
        className="bg-white dark:bg-gray-800 rounded-lg shadow-xl max-w-3xl w-full max-h-[90vh] flex flex-col"
        onClick={(e) => e.stopPropagation()}
      >
        <div className="border-b border-gray-200 dark:border-gray-700 px-6 py-4 flex items-start justify-between rounded-t-lg">
          <div>
            <h2 className="text-lg font-semibold text-gray-900 dark:text-white">{title}</h2>
            <p className="text-xs text-gray-500 dark:text-gray-400 mt-1">
              Rendered in an isolated frame. Mail clients vary — Outlook drops gradients and slanted edges.
            </p>
          </div>
          <button onClick={onClose} className="text-gray-400 hover:text-gray-600 dark:hover:text-gray-200" aria-label="Close">
            <svg className="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
            </svg>
          </button>
        </div>
        <div className="flex-1 min-h-0 bg-gray-100 dark:bg-gray-900 p-2">
          <iframe
            title={title}
            sandbox=""
            srcDoc={html}
            className="w-full h-[70vh] bg-white rounded border border-gray-200 dark:border-gray-700"
          />
        </div>
        <div className="bg-gray-50 dark:bg-gray-900 px-6 py-3 border-t border-gray-200 dark:border-gray-700 flex justify-end rounded-b-lg">
          <button
            onClick={onClose}
            className="px-3 py-2 text-sm bg-gray-200 dark:bg-gray-700 text-gray-800 dark:text-gray-100 rounded hover:bg-gray-300 dark:hover:bg-gray-600"
          >
            Close
          </button>
        </div>
      </div>
    </div>
  );
}
