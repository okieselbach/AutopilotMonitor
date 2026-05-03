"use client";

import { useState } from "react";
import Link from "next/link";
import { useParams, useSearchParams } from "next/navigation";
import { useAuth } from "@/contexts/AuthContext";
import { useDecisionGraph } from "./hooks/useDecisionGraph";
import { useSessionSignals } from "./hooks/useSessionSignals";
import { useSessionAnchorEvents } from "./hooks/useSessionAnchorEvents";
import { DecisionGraph } from "./components/DecisionGraph";
import { SignalStream } from "./components/SignalStream";
import { LifecycleAnchors } from "./components/LifecycleAnchors";

type Tab = "graph" | "signals" | "anchors" | "verifier";

const TAB_IDS: ReadonlySet<Tab> = new Set<Tab>(["graph", "signals", "anchors", "verifier"]);

function isTab(value: string | null): value is Tab {
  return value !== null && (TAB_IDS as Set<string>).has(value);
}

export default function InspectorPage() {
  const params = useParams();
  const sessionId = (params?.sessionId as string) ?? "";
  const { getAccessToken } = useAuth();

  // Deep-link via ?tab=anchors (FailureSnapshotBlock uses this). Query is read
  // once on mount; subsequent tab clicks live in component state only — no
  // history churn for casual clicks.
  const searchParams = useSearchParams();
  const initialTab = (() => {
    const fromQuery = searchParams?.get("tab") ?? null;
    return isTab(fromQuery) ? fromQuery : "graph";
  })();
  const [activeTab, setActiveTab] = useState<Tab>(initialTab);

  const decisionGraph = useDecisionGraph({ sessionId, getAccessToken });
  const signalsState = useSessionSignals({ sessionId, getAccessToken });
  const anchorsState = useSessionAnchorEvents({ sessionId, getAccessToken });

  // Lineage check: an empty Nodes list means the session has no V2 decision
  // data. Either it's a V1-agent session, or the agent never emitted a
  // transition (incomplete telemetry). Both are explained the same way to the
  // user — we don't try to disambiguate without a real AgentLineage field.
  const hasV2Data =
    !!decisionGraph.graph && decisionGraph.graph.nodes.length > 0;

  return (
    <main className="mx-auto max-w-[1400px] p-6 space-y-4">
      <header className="flex items-center justify-between">
        <div>
          <div className="text-sm text-gray-500">
            <Link href={`/sessions/${sessionId}`} className="hover:underline">
              ← Session {sessionId.substring(0, 8)}
            </Link>
          </div>
          <h1 className="text-2xl font-semibold">Decision Inspector</h1>
          <p className="text-sm text-gray-600">
            Global Admin only · v1 — Plan §M6
          </p>
        </div>
        {decisionGraph.graph?.reducerVersion && (
          <div className="text-xs text-gray-500">
            ReducerVersion:{" "}
            <code className="font-mono">
              {decisionGraph.graph.reducerVersion}
            </code>
          </div>
        )}
      </header>

      {decisionGraph.loading ? (
        <div className="rounded border border-gray-200 bg-white p-8 text-center text-gray-500">
          Loading decision graph…
        </div>
      ) : decisionGraph.error ? (
        <div className="rounded border border-red-200 bg-red-50 p-4 text-red-700">
          <strong>Failed to load:</strong> {decisionGraph.error}
        </div>
      ) : !hasV2Data ? (
        <NoV2DataNotice sessionId={sessionId} />
      ) : (
        <>
          <Tabs activeTab={activeTab} onChange={setActiveTab} />

          {activeTab === "graph" && (
            <DecisionGraph
              graph={decisionGraph.graph!}
              truncated={decisionGraph.truncated}
            />
          )}

          {activeTab === "signals" && (
            <SignalStream
              signals={signalsState.signals}
              count={signalsState.count}
              truncated={signalsState.truncated}
              loading={signalsState.loading}
              error={signalsState.error}
            />
          )}

          {activeTab === "anchors" && (
            <LifecycleAnchors
              anchors={anchorsState.anchors}
              totalEvents={anchorsState.totalEvents}
              loading={anchorsState.loading}
              error={anchorsState.error}
            />
          )}

          {activeTab === "verifier" && (
            <div className="rounded border border-gray-200 bg-white p-8 text-center text-gray-500">
              VerifierReport — not implemented in v1 (deferred)
            </div>
          )}
        </>
      )}
    </main>
  );
}

function Tabs({
  activeTab,
  onChange,
}: {
  activeTab: Tab;
  onChange: (tab: Tab) => void;
}) {
  const tabs: { id: Tab; label: string }[] = [
    { id: "graph", label: "Decision Graph" },
    { id: "signals", label: "Signal Stream" },
    { id: "anchors", label: "Lifecycle Anchors" },
    { id: "verifier", label: "Verifier Report" },
  ];

  return (
    <div className="border-b border-gray-200">
      <nav className="-mb-px flex gap-6">
        {tabs.map((t) => (
          <button
            key={t.id}
            onClick={() => onChange(t.id)}
            className={`border-b-2 px-1 py-2 text-sm font-medium transition-colors ${
              activeTab === t.id
                ? "border-blue-600 text-blue-600"
                : "border-transparent text-gray-500 hover:border-gray-300 hover:text-gray-700"
            }`}
          >
            {t.label}
          </button>
        ))}
      </nav>
    </div>
  );
}

function NoV2DataNotice({ sessionId }: { sessionId: string }) {
  return (
    <div className="rounded border border-amber-200 bg-amber-50 p-6 text-amber-900">
      <h2 className="font-semibold">No V2 decision data for this session.</h2>
      <p className="mt-2 text-sm">
        The Inspector relies on the V2-agent's signal log and decision journal.
        This session likely ran on the legacy (V1) agent, or the V2 telemetry
        never reached the backend. Use the regular{" "}
        <Link
          href={`/sessions/${sessionId}`}
          className="underline hover:text-amber-700"
        >
          session detail page
        </Link>{" "}
        for V1 sessions — it reads from the events feed.
      </p>
    </div>
  );
}
