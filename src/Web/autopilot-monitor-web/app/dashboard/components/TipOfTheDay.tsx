"use client";

import { useState, useEffect } from "react";

const tips = [
  "Auto-set the device timezone under Settings \u2192 Agent Settings.",
  "Use Analyze Rules to automatically flag enrollment issues before users report them.",
  "Check the Changelog page to stay up to date with the latest platform changes.",
  "Enable Diagnostics to collect custom log paths from your devices on demand.",
  "The Geographic view shows where your devices are enrolling on a live map.",
  "The Software Analyzer detects installed software across enrollments \u2014 find open vulnerabilities fast.",
  "Use the Local Admin Analyzer to spot legitimate vs. unexpected local admin accounts.",
  "Connect Teams or Slack under Notifications to get real-time enrollment alerts in your channel.",
  "Share the Progress Portal link with your team \u2014 anyone can check enrollment status by serial number.",
  "Missing an event in your sessions? Use Gather Rules to collect it.",
  "Analyze Rules are continuously refined \u2014 analysis results may shift as rules improve. This is normal.",
  "Check Service Announcements in the docs for external changes (e.g.\u00a0Microsoft updates) that may affect your data.",
  "Check the Intune bootstrap script (Install-AutopilotMonitor.ps1) from time to time \u2014 a newer version may be available in the repository.",
  "Spotted a session with the wrong status? Use Report Session to flag it to the team.",
];

export default function TipOfTheDay() {
  const [tip, setTip] = useState("");

  useEffect(() => {
    setTip(tips[Math.floor(Math.random() * tips.length)]);
  }, []);

  if (!tip) return null;

  return (
    <div className="mt-3 mb-1 flex items-center justify-center gap-2 text-xs text-gray-400 dark:text-gray-500 select-none">
      <svg
        className="w-3.5 h-3.5 text-amber-400 shrink-0"
        fill="none"
        stroke="currentColor"
        viewBox="0 0 24 24"
      >
        <path
          strokeLinecap="round"
          strokeLinejoin="round"
          strokeWidth={2}
          d="M9.663 17h4.673M12 3v1m6.364 1.636l-.707.707M21 12h-1M4 12H3m3.343-5.657l-.707-.707m2.828 9.9a5.002 5.002 0 017.072 0l.548.547A3.374 3.374 0 0114.81 21H9.19a3.374 3.374 0 01-2.384-5.76l.55-.547z"
        />
      </svg>
      <span>
        <span className="font-medium text-gray-500 dark:text-gray-400">Tip:</span>{" "}
        {tip}
      </span>
    </div>
  );
}
