"use client";

import Link from "next/link";
import { DOCS_URL } from "@/utils/config";

export function WelcomeMessage() {
  return (
    <div className="bg-white shadow rounded-lg overflow-hidden">
      {/* Top accent bar */}
      <div className="h-1 bg-gradient-to-r from-blue-500 via-indigo-500 to-purple-500" />
      <div className="p-6 sm:p-8">
        {/* Header */}
        <div className="flex items-start gap-4 mb-6">
          <div className="shrink-0 w-10 h-10 rounded-xl bg-blue-100 flex items-center justify-center">
            <svg className="w-5 h-5 text-blue-600" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
              <path strokeLinecap="round" strokeLinejoin="round" d="M9 12l2 2 4-4m6 2a9 9 0 11-18 0 9 9 0 0118 0z" />
            </svg>
          </div>
          <div>
            <h2 className="text-xl font-semibold text-gray-900 leading-tight">Welcome to Autopilot Monitor</h2>
            <p className="mt-1 text-sm text-gray-500">Your tenant is connected — deploy the agent to start monitoring enrollments.</p>
          </div>
        </div>

        {/* Steps */}
        <ol className="space-y-3 mb-7">
          <li className="flex items-start gap-3">
            <span className="shrink-0 mt-0.5 w-5 h-5 rounded-full bg-blue-600 text-white text-[10px] font-bold flex items-center justify-center">1</span>
            <p className="text-sm text-gray-700">
              Follow the{" "}
              <a href={`${DOCS_URL}/getting-started/portal-setup`} target="_blank" rel="noopener noreferrer" className="text-blue-600 hover:underline font-medium">setup guide</a>
              {" "}to configure your tenant and grant the required permissions.
            </p>
          </li>
          <li className="flex items-start gap-3">
            <span className="shrink-0 mt-0.5 w-5 h-5 rounded-full bg-blue-600 text-white text-[10px] font-bold flex items-center justify-center">2</span>
            <p className="text-sm text-gray-700">
              Deploy the{" "}
              <a href={`${DOCS_URL}/getting-started/deploy-the-agent`} target="_blank" rel="noopener noreferrer" className="text-blue-600 hover:underline font-medium">Intune bootstrapper</a>
              {" "}to your Autopilot device groups — the agent installs automatically on first boot.
            </p>
          </li>
          <li className="flex items-start gap-3">
            <span className="shrink-0 mt-0.5 w-5 h-5 rounded-full bg-blue-600 text-white text-[10px] font-bold flex items-center justify-center">3</span>
            <p className="text-sm text-gray-700">Enrollment sessions will appear here in real time as devices go through the enrollment process.</p>
          </li>
        </ol>

        {/* Quick links */}
        <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
          <a
            href={DOCS_URL} target="_blank" rel="noopener noreferrer"
            className="group flex items-center gap-3 rounded-lg border border-gray-200 bg-gray-50 px-4 py-3 hover:border-blue-300 hover:bg-blue-50 transition-colors"
          >
            <span className="shrink-0 w-8 h-8 rounded-lg bg-white border border-gray-200 group-hover:border-blue-200 flex items-center justify-center shadow-sm">
              <svg className="w-4 h-4 text-blue-600" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                <path strokeLinecap="round" strokeLinejoin="round" d="M12 6.253v13m0-13C10.832 5.477 9.246 5 7.5 5S4.168 5.477 3 6.253v13C4.168 18.477 5.754 18 7.5 18s3.332.477 4.5 1.253m0-13C13.168 5.477 14.754 5 16.5 5c1.747 0 3.332.477 4.5 1.253v13C19.832 18.477 18.247 18 16.5 18c-1.746 0-3.332.477-4.5 1.253" />
              </svg>
            </span>
            <div>
              <p className="text-sm font-medium text-gray-900">Documentation</p>
              <p className="text-xs text-gray-500">Setup and configuration guides</p>
            </div>
            <svg className="w-4 h-4 text-gray-300 group-hover:text-blue-400 ml-auto shrink-0 transition-colors" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
              <path strokeLinecap="round" strokeLinejoin="round" d="M9 5l7 7-7 7" />
            </svg>
          </a>
          <a
            href="https://github.com/okieselbach/Autopilot-Monitor"
            target="_blank"
            rel="noopener noreferrer"
            className="group flex items-center gap-3 rounded-lg border border-gray-200 bg-gray-50 px-4 py-3 hover:border-gray-400 hover:bg-gray-100 transition-colors"
          >
            <span className="shrink-0 w-8 h-8 rounded-lg bg-white border border-gray-200 group-hover:border-gray-300 flex items-center justify-center shadow-sm">
              <svg className="w-4 h-4 text-gray-700" fill="currentColor" viewBox="0 0 24 24">
                <path d="M12 0C5.37 0 0 5.37 0 12c0 5.31 3.435 9.795 8.205 11.385.6.105.825-.255.825-.57 0-.285-.015-1.23-.015-2.235-3.015.555-3.795-.735-4.035-1.41-.135-.345-.72-1.41-1.23-1.695-.42-.225-1.02-.78-.015-.795.945-.015 1.62.87 1.845 1.23 1.08 1.815 2.805 1.305 3.495.99.105-.78.42-1.305.765-1.605-2.67-.3-5.46-1.335-5.46-5.925 0-1.305.465-2.385 1.23-3.225-.12-.3-.54-1.53.12-3.18 0 0 1.005-.315 3.3 1.23.96-.27 1.98-.405 3-.405s2.04.135 3 .405c2.295-1.56 3.3-1.23 3.3-1.23.66 1.65.24 2.88.12 3.18.765.84 1.23 1.905 1.23 3.225 0 4.605-2.805 5.625-5.475 5.925.435.375.81 1.095.81 2.22 0 1.605-.015 2.895-.015 3.3 0 .315.225.69.825.57A12.02 12.02 0 0024 12c0-6.63-5.37-12-12-12z" />
              </svg>
            </span>
            <div>
              <p className="text-sm font-medium text-gray-900">GitHub Repository</p>
              <p className="text-xs text-gray-500">Source code and issue tracking</p>
            </div>
            <svg className="w-4 h-4 text-gray-300 group-hover:text-gray-500 ml-auto shrink-0 transition-colors" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
              <path strokeLinecap="round" strokeLinejoin="round" d="M9 5l7 7-7 7" />
            </svg>
          </a>
        </div>
      </div>
    </div>
  );
}
