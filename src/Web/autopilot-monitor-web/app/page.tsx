import { PublicSiteNavbar } from "../components/PublicSiteNavbar";
import { AuthGate } from "../components/landing/AuthGate";
import { LoginButton } from "../components/landing/LoginButton";
import { PlatformStats } from "../components/landing/PlatformStats";
import { DOCS_URL } from "@/utils/config";

const QUICK_START = [
  {
    title: "Sign in and grant access",
    description: "Authenticate with Microsoft and approve tenant access once.",
  },
  {
    title: "Deploy bootstrapper in Intune",
    description: "Assign the bootstrap script to your Autopilot scope.",
  },
  {
    title: "Watch live telemetry",
    description: "Track phases, apps, failures, and actions in real time.",
  },
];

const FLOW_STEPS = [
  {
    title: "Intune assignment",
    description: "Target the bootstrapper script to your selected Autopilot device groups.",
    icon: "users",
  },
  {
    title: "Bootstrapper execution",
    description: "Device runs the bootstrapper script and installs Autopilot Monitor Agent.",
    icon: "code",
  },
  {
    title: "Live monitoring",
    description: "Phase transitions, app installs, and progress are captured continuously.",
    icon: "monitor",
  },
  {
    title: "Event upload",
    description: "Predefined and custom events are uploaded to the backend pipeline.",
    icon: "cloud",
  },
  {
    title: "Rule analysis",
    description: "Backend correlates phases and runs analyze rules for instant insights.",
    icon: "rules",
  },
  {
    title: "Completion and notifications",
    description: "Session status is finalized and Teams notifications are triggered.",
    icon: "bell",
  },
  {
    title: "Diagnostics download",
    description: "Grab diagnostic bundles quickly for fast root-cause validation.",
    icon: "download",
  },
];

function StepIcon({ icon }: { icon: string }) {
  switch (icon) {
    case "users":
      return (
        <svg className="w-4 h-4 text-blue-700" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
          <path strokeLinecap="round" strokeLinejoin="round" d="M17 20h5v-1a4 4 0 00-5-3.87M9 20H2v-1a4 4 0 015-3.87m9-5.13a4 4 0 11-8 0 4 4 0 018 0z" />
        </svg>
      );
    case "code":
      return (
        <svg className="w-4 h-4 text-blue-700" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
          <path strokeLinecap="round" strokeLinejoin="round" d="m8 9-3 3 3 3m8-6 3 3-3 3M13 7l-2 10" />
        </svg>
      );
    case "monitor":
      return (
        <svg className="w-4 h-4 text-blue-700" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
          <path strokeLinecap="round" strokeLinejoin="round" d="M3 4h18v12H3zM8 20h8m-4-4v4m-3-8 2-2 2 3 3-4 2 3" />
        </svg>
      );
    case "cloud":
      return (
        <svg className="w-4 h-4 text-blue-700" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
          <path strokeLinecap="round" strokeLinejoin="round" d="M7 18a4 4 0 01-.3-8A5 5 0 1117 8h1a4 4 0 010 8h-3m-3 0v-7m0 7-3-3m3 3 3-3" />
        </svg>
      );
    case "rules":
      return (
        <svg className="w-4 h-4 text-blue-700" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
          <path strokeLinecap="round" strokeLinejoin="round" d="M9 5h10M9 9h10M9 13h10M9 17h10M4 5h.01M4 9h.01M4 13h.01M4 17h.01" />
        </svg>
      );
    case "bell":
      return (
        <svg className="w-4 h-4 text-blue-700" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
          <path strokeLinecap="round" strokeLinejoin="round" d="M15 17h5l-1.4-1.4A2 2 0 0118 14.2V11a6 6 0 10-12 0v3.2c0 .5-.2 1-.6 1.4L4 17h5m6 0a3 3 0 11-6 0h6z" />
        </svg>
      );
    default:
      return (
        <svg className="w-4 h-4 text-blue-700" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
          <path strokeLinecap="round" strokeLinejoin="round" d="M12 5v10m0 0-3-3m3 3 3-3M5 19h14" />
        </svg>
      );
  }
}

export default function LandingPage() {
  return (
    <div className="landing-page min-h-screen bg-gradient-to-br from-blue-50 via-indigo-50 to-purple-50">
      {/* Client component: handles auth redirect + loading overlay */}
      <AuthGate />

      <PublicSiteNavbar showSectionLinks={true} />

      {/* Hero Section */}
      <div className="pt-20 pb-20 px-6">
        <div className="max-w-7xl mx-auto">
          <div className="text-center max-w-4xl mx-auto">
            <div className="mb-7 inline-flex items-center gap-3 rounded-2xl border border-blue-300/70 bg-gradient-to-r from-blue-50 via-indigo-50 to-blue-50 px-4 py-2.5 shadow-md ring-1 ring-blue-200/60">
              <span className="inline-flex h-6 w-6 items-center justify-center rounded-full bg-blue-600 text-white shadow-sm">
                <svg className="w-3.5 h-3.5" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2.2}>
                  <path strokeLinecap="round" strokeLinejoin="round" d="M17 20h5v-1a4 4 0 00-5-3.87M9 20H2v-1a4 4 0 015-3.87m9-5.13a4 4 0 11-8 0 4 4 0 018 0z" />
                </svg>
              </span>
              <p className="text-sm font-semibold text-blue-900">
                Community-driven Analyze Rules support
                <span className="font-normal text-blue-800">: discover, build, and share rules for everyone.</span>
              </p>
            </div>
            <div className="relative inline-block mb-6">
              <h1 className="text-6xl font-bold text-gray-900 leading-tight">
              Advanced Monitoring for
              <span className="bg-gradient-to-r from-blue-600 to-indigo-600 bg-clip-text text-transparent"> Windows Enrollments</span>
              </h1>
              <div className="pointer-events-none absolute -right-10 -top-1 rotate-[13deg] inline-flex items-center rounded-md border border-amber-300/80 bg-gradient-to-r from-amber-500 to-orange-500 px-3 py-1 shadow-md">
                <span className="text-[10px] font-bold uppercase tracking-[0.2em] text-white whitespace-nowrap">
                  Private Preview Running
                </span>
              </div>
            </div>
            <p className="text-xl text-gray-600 mb-8 leading-relaxed">
              Real-time insights, intelligent troubleshooting, and comprehensive analytics for your Autopilot deployments.
              Monitor every phase, run customizable analyze rules, and resolve issues faster than ever before.
            </p>
            <div className="flex items-center justify-center space-x-4">
              <LoginButton
                className="px-8 py-4 bg-gradient-to-r from-blue-600 to-indigo-600 text-white rounded-lg font-semibold text-lg shadow-xl hover:shadow-2xl transform hover:-translate-y-0.5 transition-all flex items-center space-x-2"
              >
                <svg className="w-5 h-5" fill="currentColor" viewBox="0 0 20 20">
                  <path d="M10 12a2 2 0 100-4 2 2 0 000 4z" />
                  <path fillRule="evenodd" d="M.458 10C1.732 5.943 5.522 3 10 3s8.268 2.943 9.542 7c-1.274 4.057-5.064 7-9.542 7S1.732 14.057.458 10zM14 10a4 4 0 11-8 0 4 4 0 018 0z" clipRule="evenodd" />
                </svg>
                <span>Get Started</span>
              </LoginButton>
            </div>
            <p className="mt-4 text-sm text-gray-500">
              Free to use • Open-Source • Sign in to request early access
            </p>

            {/* Platform Stats - client component fetches live data */}
            <PlatformStats />

            {/* Product Preview Showcase */}
            <div className="mt-14 relative max-w-5xl mx-auto">
              {/* Glow effects */}
              <div className="absolute -top-10 left-1/2 -translate-x-1/2 w-[600px] h-[300px] bg-gradient-to-br from-blue-400/20 via-indigo-400/15 to-purple-400/10 blur-3xl rounded-full pointer-events-none" />

              {/* Main screenshot — Fleet Health Dashboard */}
              <div className="relative z-10 rounded-xl border border-gray-200/80 bg-white shadow-2xl overflow-hidden">
                {/* Browser chrome */}
                <div className="flex items-center gap-1.5 px-4 py-2.5 bg-gray-50 border-b border-gray-200">
                  <div className="w-2.5 h-2.5 rounded-full bg-red-400" />
                  <div className="w-2.5 h-2.5 rounded-full bg-amber-400" />
                  <div className="w-2.5 h-2.5 rounded-full bg-green-400" />
                  <div className="ml-3 flex-1 bg-gray-200/80 rounded-md px-3 py-1 text-[10px] text-gray-400 font-mono">autopilotmonitor.com/fleet-health</div>
                </div>
                {/* Fleet Health mock content */}
                <div className="p-5 md:p-7">
                  <div className="flex items-center justify-between mb-5">
                    <div className="flex items-center gap-2.5">
                      <div className="w-6 h-6 rounded bg-blue-100 flex items-center justify-center">
                        <svg className="w-3.5 h-3.5 text-blue-600" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}><path strokeLinecap="round" strokeLinejoin="round" d="M9 19v-6a2 2 0 00-2-2H5a2 2 0 00-2 2v6m6 0V9a2 2 0 012-2h2a2 2 0 012 2v10m-6 0h6m4-10v10" /></svg>
                      </div>
                      <h4 className="text-lg font-bold text-gray-900">Fleet Health</h4>
                    </div>
                    <div className="flex gap-1.5">
                      <span className="px-3 py-1 text-[10px] font-semibold bg-blue-600 text-white rounded-md">Last 7 Days</span>
                      <span className="px-3 py-1 text-[10px] font-medium text-gray-500 bg-gray-100 rounded-md">Last 30 Days</span>
                      <span className="px-3 py-1 text-[10px] font-medium text-gray-500 bg-gray-100 rounded-md hidden sm:block">Last 90 Days</span>
                    </div>
                  </div>
                  {/* KPI cards */}
                  <div className="grid grid-cols-2 md:grid-cols-4 gap-3 mb-6">
                    <div className="rounded-lg border-l-4 border-l-green-500 border border-gray-100 bg-green-50/50 p-3">
                      <p className="text-[10px] text-gray-500 mb-0.5">Success Rate</p>
                      <p className="text-2xl font-bold text-green-600">80.0%</p>
                      <p className="text-[9px] text-gray-400">8 of 10 enrollments</p>
                    </div>
                    <div className="rounded-lg border-l-4 border-l-blue-500 border border-gray-100 bg-blue-50/50 p-3">
                      <p className="text-[10px] text-gray-500 mb-0.5">Avg. Enrollment Time</p>
                      <p className="text-2xl font-bold text-blue-600">18 min</p>
                      <p className="text-[9px] text-gray-400">Completed enrollments</p>
                    </div>
                    <div className="rounded-lg border-l-4 border-l-red-500 border border-gray-100 bg-red-50/50 p-3">
                      <p className="text-[10px] text-gray-500 mb-0.5">Failed</p>
                      <p className="text-2xl font-bold text-red-500">2</p>
                      <p className="text-[9px] text-gray-400">Needs attention</p>
                    </div>
                    <div className="rounded-lg border-l-4 border-l-indigo-500 border border-gray-100 bg-indigo-50/50 p-3">
                      <p className="text-[10px] text-gray-500 mb-0.5">Active Now</p>
                      <p className="text-2xl font-bold text-indigo-600">0</p>
                      <p className="text-[9px] text-gray-400">Currently enrolling</p>
                    </div>
                  </div>
                  {/* Chart area */}
                  <div className="rounded-lg border border-gray-100 bg-gray-50/60 p-4">
                    <p className="text-sm font-semibold text-gray-800 mb-3">Enrollments Timeline</p>
                    <div className="flex items-end gap-3 h-28 px-2">
                      {[0, 0, 0, { g: 65, r: 25 }, { g: 50, r: 30 }, 0, { g: 40, r: 20 }, 0].map((bar, i) => (
                        <div key={i} className="flex-1 flex flex-col items-center gap-0.5">
                          {typeof bar === 'object' ? (
                            <div className="w-full flex flex-col gap-px">
                              <div className="w-full rounded-t bg-green-400" style={{ height: `${bar.g}px` }} />
                              <div className="w-full rounded-b bg-red-300" style={{ height: `${bar.r}px` }} />
                            </div>
                          ) : (
                            <div className="w-full rounded bg-gray-200/60" style={{ height: '2px' }} />
                          )}
                          <span className="text-[8px] text-gray-400 mt-1">{['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun', 'Mon'][i]}</span>
                        </div>
                      ))}
                    </div>
                    <div className="flex items-center gap-4 mt-2 ml-2">
                      <span className="flex items-center gap-1 text-[9px] text-gray-500"><span className="w-2 h-2 rounded-sm bg-green-400" />Success (8)</span>
                      <span className="flex items-center gap-1 text-[9px] text-gray-500"><span className="w-2 h-2 rounded-sm bg-red-300" />Failed (2)</span>
                    </div>
                  </div>
                </div>
              </div>

              {/* Floating card — Session Details (overlapping bottom-left) */}
              <div className="absolute -bottom-8 -left-4 md:-left-8 z-20 w-64 md:w-80 rounded-xl border border-gray-200/80 bg-white shadow-xl overflow-hidden transform rotate-[-2deg] hover:rotate-0 transition-transform duration-300">
                <div className="flex items-center gap-1.5 px-3 py-1.5 bg-gray-50 border-b border-gray-100">
                  <div className="w-2 h-2 rounded-full bg-red-400" />
                  <div className="w-2 h-2 rounded-full bg-amber-400" />
                  <div className="w-2 h-2 rounded-full bg-green-400" />
                  <span className="ml-2 text-[8px] text-gray-400 font-mono">Session Details</span>
                </div>
                <div className="p-3.5">
                  <p className="text-xs font-bold text-gray-900 mb-2">Enrollment Progress</p>
                  {/* Progress steps */}
                  <div className="flex items-center gap-0">
                    {['Start', 'Prep', 'Setup', 'Apps', 'Account', 'Apps', 'Final', 'Done'].map((step, i) => (
                      <div key={i} className="flex items-center">
                        <div className="w-5 h-5 rounded-full bg-green-500 flex items-center justify-center shrink-0">
                          <svg className="w-2.5 h-2.5 text-white" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={3}><path strokeLinecap="round" strokeLinejoin="round" d="M5 13l4 4L19 7" /></svg>
                        </div>
                        {i < 7 && <div className="w-2 md:w-3 h-0.5 bg-green-400" />}
                      </div>
                    ))}
                  </div>
                  <div className="flex justify-between mt-1 px-0.5">
                    {['Start', '', '', '', '', '', '', 'Done'].map((label, i) => (
                      <span key={i} className="text-[7px] text-gray-400">{label}</span>
                    ))}
                  </div>
                  {/* Analysis result */}
                  <div className="mt-3 rounded-lg border-l-3 border-l-orange-400 bg-orange-50 p-2">
                    <div className="flex items-center gap-1.5 mb-0.5">
                      <span className="px-1.5 py-0.5 text-[7px] font-bold bg-orange-500 text-white rounded">HIGH</span>
                      <span className="text-[8px] text-gray-500 font-mono">ANALYZE-APP-006</span>
                    </div>
                    <p className="text-[10px] font-semibold text-gray-800">Reboot Required Loop</p>
                    <div className="flex items-center gap-1 mt-1">
                      <span className="text-[8px] text-gray-400">Confidence:</span>
                      <div className="w-12 h-1.5 bg-gray-200 rounded-full overflow-hidden"><div className="w-[70%] h-full bg-orange-500 rounded-full" /></div>
                      <span className="text-[8px] text-gray-500">70%</span>
                    </div>
                  </div>
                </div>
              </div>

              {/* Floating card — Performance Metrics (overlapping bottom-right) */}
              <div className="absolute -bottom-6 -right-4 md:-right-6 z-20 w-60 md:w-72 rounded-xl border border-gray-200/80 bg-white shadow-xl overflow-hidden transform rotate-[2deg] hover:rotate-0 transition-transform duration-300">
                <div className="flex items-center gap-1.5 px-3 py-1.5 bg-gray-50 border-b border-gray-100">
                  <div className="w-2 h-2 rounded-full bg-red-400" />
                  <div className="w-2 h-2 rounded-full bg-amber-400" />
                  <div className="w-2 h-2 rounded-full bg-green-400" />
                  <span className="ml-2 text-[8px] text-gray-400 font-mono">Performance Metrics</span>
                </div>
                <div className="p-3.5">
                  <div className="grid grid-cols-2 gap-2">
                    <div className="rounded-lg border border-gray-100 p-2">
                      <div className="flex items-center justify-between mb-1">
                        <span className="text-[9px] text-gray-500">CPU</span>
                        <span className="text-[10px] font-bold text-red-500">92%</span>
                      </div>
                      <div className="h-6 flex items-end gap-px">
                        {[40, 55, 70, 85, 92, 88, 75, 60, 80, 92].map((v, i) => (
                          <div key={i} className="flex-1 rounded-t bg-red-400/70" style={{ height: `${v * 0.24}px` }} />
                        ))}
                      </div>
                    </div>
                    <div className="rounded-lg border border-gray-100 p-2">
                      <div className="flex items-center justify-between mb-1">
                        <span className="text-[9px] text-gray-500">Memory</span>
                        <span className="text-[10px] font-bold text-amber-600">77%</span>
                      </div>
                      <div className="h-6 flex items-end gap-px">
                        {[60, 65, 68, 70, 72, 75, 74, 76, 77, 77].map((v, i) => (
                          <div key={i} className="flex-1 rounded-t bg-amber-400/70" style={{ height: `${v * 0.24}px` }} />
                        ))}
                      </div>
                    </div>
                  </div>
                  {/* Download progress */}
                  <div className="mt-2.5">
                    <p className="text-[9px] font-semibold text-gray-700 mb-1.5">Download Progress</p>
                    {['RealmJoin Setup Launcher', 'BGInfo46', 'CMTrace.exe'].map((app, i) => (
                      <div key={i} className="mb-1.5">
                        <div className="flex items-center justify-between">
                          <span className="text-[8px] text-gray-600 flex items-center gap-1">
                            <svg className="w-2.5 h-2.5 text-green-500" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={3}><path strokeLinecap="round" strokeLinejoin="round" d="M5 13l4 4L19 7" /></svg>
                            {app}
                          </span>
                          <span className="text-[8px] text-green-600 font-medium">100%</span>
                        </div>
                        <div className="w-full h-1 bg-gray-100 rounded-full mt-0.5"><div className="w-full h-full bg-green-500 rounded-full" /></div>
                      </div>
                    ))}
                  </div>
                </div>
              </div>
            </div>
            {/* spacer for floating cards */}
            <div className="h-14" />
          </div>
        </div>
      </div>

      {/* Eye-Catcher Workflow Section */}
      <section id="how-it-works" className="py-20 px-6 scroll-mt-14">
        <div className="max-w-7xl mx-auto">
          <div className="text-center max-w-4xl mx-auto">
            <p className="text-sm font-semibold text-blue-600 uppercase tracking-[0.22em] mb-3">
              How It Works
            </p>
            <h2 className="text-4xl md:text-5xl font-bold text-gray-900 leading-tight">
              From Intune rollout to deep insights in minutes
            </h2>
            <p className="mt-5 text-lg text-gray-600 leading-relaxed">
              Simple onboarding, clear phase visibility, and automated analysis in one workflow.
              Deploy once, monitor everything, and react faster when issues appear.
            </p>
          </div>

          <div className="mt-10 grid grid-cols-1 md:grid-cols-3 gap-4">
            {QUICK_START.map((item, index) => (
              <div
                key={item.title}
                className="rounded-2xl border border-blue-100 bg-white/90 backdrop-blur-sm p-5 shadow-md"
              >
                <div className="inline-flex h-7 min-w-7 items-center justify-center rounded-full bg-blue-600 text-white text-xs font-bold px-2">
                  {index + 1}
                </div>
                <h3 className="mt-3 text-lg font-semibold text-gray-900">{item.title}</h3>
                <p className="mt-2 text-sm text-gray-600 leading-relaxed">{item.description}</p>
              </div>
            ))}
          </div>

          <div className="mt-8 md:mt-10 text-center">
            <p className="text-sm md:text-base text-gray-600 max-w-3xl mx-auto leading-relaxed">
              This is what happens after rollout: each phase is captured, correlated, and translated into clear, actionable insights.
            </p>
          </div>

          <div className="mt-6 md:mt-7 rounded-3xl border border-blue-100 bg-gradient-to-br from-white via-blue-50/60 to-indigo-50/60 p-5 md:p-6 shadow-xl overflow-hidden relative">
            <div className="absolute -top-16 -left-16 w-48 h-48 bg-blue-200/30 blur-3xl rounded-full pointer-events-none" />
            <div className="absolute -bottom-20 -right-16 w-56 h-56 bg-indigo-200/25 blur-3xl rounded-full pointer-events-none" />

            <div className="relative z-10">
              <div className="flex items-center justify-between gap-4 flex-wrap">
                <div>
                  <h3 className="text-gray-900 text-2xl md:text-3xl font-bold">
                    One pipeline from deployment to action
                  </h3>
                </div>
                <LoginButton
                  className="px-5 py-2.5 rounded-lg bg-gradient-to-r from-blue-600 to-indigo-600 text-white font-semibold shadow-lg hover:shadow-xl transition-all"
                >
                  Start Now
                </LoginButton>
              </div>

              <div className="mt-6 relative">
                {/* Desktop: independent left/right stacks for true vertical overlap */}
                <div className="hidden md:grid grid-cols-[minmax(0,1fr)_40px_minmax(0,1fr)] gap-4 items-start relative isolate">
                  <div className="space-y-20 relative z-30">
                    {FLOW_STEPS.filter((_, i) => i % 2 === 0).map((step, leftIndex) => {
                      const index = leftIndex * 2;
                      return (
                        <div
                          key={step.title}
                          className="step-card relative rounded-xl border border-blue-100 bg-white/90 backdrop-blur-sm p-3 md:p-3.5 shadow-md"
                          style={{ animationDelay: `${index * 0.08}s` }}
                        >
                          <div className="absolute right-[-36px] top-[calc(50%+1px)] -translate-y-1/2 h-px w-[36px] bg-blue-200 z-10" />
                          <span
                            className="dot-pulse absolute right-[-42px] top-[calc(50%-4px)] -translate-y-1/2 h-3 w-3 rounded-full bg-blue-500 ring-4 ring-white z-50"
                            style={{ animationDelay: `${index * 1.8}s` }}
                          />
                          <p className="text-blue-600 text-xs font-semibold uppercase tracking-[0.18em]">
                            Step {index + 1}
                          </p>
                          <div className="mt-1.5 flex items-start justify-between gap-3">
                            <h4 className="text-gray-900 font-semibold">{step.title}</h4>
                            <span className="inline-flex h-8 w-8 shrink-0 items-center justify-center rounded-lg border border-blue-200 bg-blue-50">
                              <StepIcon icon={step.icon} />
                            </span>
                          </div>
                          <p className="mt-1 text-sm text-gray-600 leading-relaxed">{step.description}</p>
                        </div>
                      );
                    })}
                  </div>

                  <div className="relative self-stretch z-0">
                    <div className="absolute left-1/2 -translate-x-1/2 top-2 bottom-2 w-px bg-blue-200 z-0" />
                  </div>

                  <div className="space-y-20 pt-24 relative z-30">
                    {FLOW_STEPS.filter((_, i) => i % 2 === 1).map((step, rightIndex) => {
                      const index = rightIndex * 2 + 1;
                      return (
                        <div
                          key={step.title}
                          className="step-card relative rounded-xl border border-blue-100 bg-white/90 backdrop-blur-sm p-3 md:p-3.5 shadow-md"
                          style={{ animationDelay: `${index * 0.08}s` }}
                        >
                          <div className="absolute left-[-36px] top-[calc(50%+1px)] -translate-y-1/2 h-px w-[36px] bg-blue-200 z-10" />
                          <span
                            className="dot-pulse absolute left-[-42px] top-[calc(50%-4px)] -translate-y-1/2 h-3 w-3 rounded-full bg-blue-500 ring-4 ring-white z-50"
                            style={{ animationDelay: `${index * 1.8}s` }}
                          />
                          <p className="text-blue-600 text-xs font-semibold uppercase tracking-[0.18em]">
                            Step {index + 1}
                          </p>
                          <div className="mt-1.5 flex items-start justify-between gap-3">
                            <h4 className="text-gray-900 font-semibold">{step.title}</h4>
                            <span className="inline-flex h-8 w-8 shrink-0 items-center justify-center rounded-lg border border-blue-200 bg-blue-50">
                              <StepIcon icon={step.icon} />
                            </span>
                          </div>
                          <p className="mt-1 text-sm text-gray-600 leading-relaxed">{step.description}</p>
                        </div>
                      );
                    })}
                  </div>
                </div>

                {/* Mobile: linear timeline */}
                <div className="md:hidden relative isolate">
                  <div className="absolute left-4 top-3 bottom-3 w-px bg-blue-200" />
                  <div className="space-y-5">
                    {FLOW_STEPS.map((step, index) => (
                      <div key={step.title} className="relative">
                        <div className="absolute left-4 top-[calc(2rem+1px)] h-px w-6 bg-blue-200 z-10" />
                        <span
                          className="dot-pulse absolute left-4 top-[calc(1.75rem-4px)] h-3 w-3 rounded-full bg-blue-500 ring-4 ring-white z-50"
                          style={{ animationDelay: `${index * 1.8}s` }}
                        />
                        <div
                          className="step-card ml-10 rounded-xl border border-blue-100 bg-white/90 backdrop-blur-sm p-3 shadow-md"
                          style={{ animationDelay: `${index * 0.08}s` }}
                        >
                          <p className="text-blue-600 text-xs font-semibold uppercase tracking-[0.18em]">
                            Step {index + 1}
                          </p>
                          <div className="mt-1.5 flex items-start justify-between gap-3">
                            <h4 className="text-gray-900 font-semibold">{step.title}</h4>
                            <span className="inline-flex h-8 w-8 shrink-0 items-center justify-center rounded-lg border border-blue-200 bg-blue-50">
                              <StepIcon icon={step.icon} />
                            </span>
                          </div>
                          <p className="mt-1 text-sm text-gray-600 leading-relaxed">{step.description}</p>
                        </div>
                      </div>
                    ))}
                  </div>
                </div>
              </div>

              <div className="mt-6 grid grid-cols-1 md:grid-cols-3 gap-3">
                {[
                  "Know exactly what happens at each enrollment phase",
                  "Detect app bottlenecks and policy issues immediately",
                  "Move from alert to diagnostics with minimal friction",
                ].map(item => (
                  <div key={item} className="rounded-xl border border-blue-200 bg-blue-50/80 p-3.5 text-sm text-blue-900">
                    {item}
                  </div>
                ))}
              </div>
            </div>
          </div>
        </div>
      </section>

      {/* Features Grid */}
      <div id="features" className="py-20 px-6 bg-white/50 backdrop-blur-sm scroll-mt-14">
        <div className="max-w-7xl mx-auto">
          <p className="text-sm font-semibold text-center text-blue-600 uppercase tracking-[0.22em] mb-3">
            Features
          </p>
          <h2 className="text-3xl font-bold text-center text-gray-900 mb-12">
            Everything you need for full Autopilot visibility
          </h2>
          <div className="grid grid-cols-1 md:grid-cols-3 gap-8">
            {/* Feature 1 - Real-Time Monitoring */}
            <div className="bg-white rounded-2xl p-8 shadow-lg hover:shadow-xl transition-shadow">
              <div className="w-12 h-12 bg-blue-100 rounded-xl flex items-center justify-center mb-4">
                <svg className="w-6 h-6 text-blue-600" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M13 10V3L4 14h7v7l9-11h-7z" />
                </svg>
              </div>
              <h3 className="text-xl font-semibold text-gray-900 mb-3">Real-Time Monitoring</h3>
              <p className="text-gray-600 mb-4">
                Watch Autopilot deployments in near real-time with live event streaming. Track every phase from device registration to user login.
              </p>
              <ul className="space-y-2">
                {["Live phase tracking", "Near realtime push updates", "Per-device event stream"].map(item => (
                  <li key={item} className="flex items-center text-sm text-gray-500">
                    <span className="w-1.5 h-1.5 rounded-full bg-blue-400 mr-2 shrink-0" />
                    {item}
                  </li>
                ))}
              </ul>
            </div>

            {/* Feature 2 - Rich Analytics */}
            <div className="bg-white rounded-2xl p-8 shadow-lg hover:shadow-xl transition-shadow">
              <div className="w-12 h-12 bg-indigo-100 rounded-xl flex items-center justify-center mb-4">
                <svg className="w-6 h-6 text-indigo-600" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 19v-6a2 2 0 00-2-2H5a2 2 0 00-2 2v6a2 2 0 002 2h2a2 2 0 002-2zm0 0V9a2 2 0 012-2h2a2 2 0 012 2v10m-6 0a2 2 0 002 2h2a2 2 0 002-2m0 0V5a2 2 0 012-2h2a2 2 0 012 2v14a2 2 0 01-2 2h-2a2 2 0 01-2-2z" />
                </svg>
              </div>
              <h3 className="text-xl font-semibold text-gray-900 mb-3">Rich Analytics</h3>
              <p className="text-gray-600 mb-4">
                Comprehensive metrics on deployment success rates, performance trends, and hardware insights — powered by customizable analyze rules.
              </p>
              <ul className="space-y-2">
                {["Customizable analyze rules", "Success & failure rates", "Hardware model insights"].map(item => (
                  <li key={item} className="flex items-center text-sm text-gray-500">
                    <span className="w-1.5 h-1.5 rounded-full bg-indigo-400 mr-2 shrink-0" />
                    {item}
                  </li>
                ))}
              </ul>
            </div>

            {/* Feature 3 - Fleet Health */}
            <div className="bg-white rounded-2xl p-8 shadow-lg hover:shadow-xl transition-shadow">
              <div className="w-12 h-12 bg-purple-100 rounded-xl flex items-center justify-center mb-4">
                <svg className="w-6 h-6 text-purple-600" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M4.318 6.318a4.5 4.5 0 000 6.364L12 20.364l7.682-7.682a4.5 4.5 0 00-6.364-6.364L12 7.636l-1.318-1.318a4.5 4.5 0 00-6.364 0z" />
                </svg>
              </div>
              <h3 className="text-xl font-semibold text-gray-900 mb-3">Fleet Health</h3>
              <p className="text-gray-600 mb-4">
                Live overview of your entire device fleet. Spot unhealthy devices, track blocked enrollments, and monitor per-tenant health at a glance.
              </p>
              <ul className="space-y-2">
                {["Device health scoring", "Blocked device detection", "Tenant-level overview"].map(item => (
                  <li key={item} className="flex items-center text-sm text-gray-500">
                    <span className="w-1.5 h-1.5 rounded-full bg-purple-400 mr-2 shrink-0" />
                    {item}
                  </li>
                ))}
              </ul>
            </div>

            {/* Feature 4 - Diagnostics Collection */}
            <div className="bg-white rounded-2xl p-8 shadow-lg hover:shadow-xl transition-shadow">
              <div className="w-12 h-12 bg-green-100 rounded-xl flex items-center justify-center mb-4">
                <svg className="w-6 h-6 text-green-600" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 17v-2m3 2v-4m3 4v-6m2 10H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z" />
                </svg>
              </div>
              <h3 className="text-xl font-semibold text-gray-900 mb-3">Diagnostics Collection</h3>
              <p className="text-gray-600 mb-4">
                Trigger on-demand diagnostic uploads directly from the portal. Collect ETL logs, event logs, and system info from any enrolled device.
              </p>
              <ul className="space-y-2">
                {["On-demand ZIP upload", "ETL & event log collection", "Per-device retrieval"].map(item => (
                  <li key={item} className="flex items-center text-sm text-gray-500">
                    <span className="w-1.5 h-1.5 rounded-full bg-green-400 mr-2 shrink-0" />
                    {item}
                  </li>
                ))}
              </ul>
            </div>

            {/* Feature 5 - Event Timeline */}
            <div className="bg-white rounded-2xl p-8 shadow-lg hover:shadow-xl transition-shadow">
              <div className="w-12 h-12 bg-orange-100 rounded-xl flex items-center justify-center mb-4">
                <svg className="w-6 h-6 text-orange-600" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M7 8h10M7 12h4m1 8l-4-4H5a2 2 0 01-2-2V6a2 2 0 012-2h14a2 2 0 012 2v8a2 2 0 01-2 2h-3l-4 4z" />
                </svg>
              </div>
              <h3 className="text-xl font-semibold text-gray-900 mb-3">Event Timeline</h3>
              <p className="text-gray-600 mb-4">
                Detailed event timeline for every deployment session. Drill down into events, errors, and warnings to troubleshoot efficiently.
              </p>
              <ul className="space-y-2">
                {["Phase-by-phase breakdown", "App install details", "Error & warning highlights"].map(item => (
                  <li key={item} className="flex items-center text-sm text-gray-500">
                    <span className="w-1.5 h-1.5 rounded-full bg-orange-400 mr-2 shrink-0" />
                    {item}
                  </li>
                ))}
              </ul>
            </div>

            {/* Feature 6 - Audit Logging */}
            <div className="bg-white rounded-2xl p-8 shadow-lg hover:shadow-xl transition-shadow">
              <div className="w-12 h-12 bg-red-100 rounded-xl flex items-center justify-center mb-4">
                <svg className="w-6 h-6 text-red-600" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 12h6m-6 4h6m2 5H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z" />
                </svg>
              </div>
              <h3 className="text-xl font-semibold text-gray-900 mb-3">Audit Logging</h3>
              <p className="text-gray-600 mb-4">
                Complete audit trail of all actions and changes. Meet compliance requirements with detailed logging and data retention policies.
              </p>
              <ul className="space-y-2">
                {["Admin action history", "Configurable retention", "Tamper-evident records"].map(item => (
                  <li key={item} className="flex items-center text-sm text-gray-500">
                    <span className="w-1.5 h-1.5 rounded-full bg-red-400 mr-2 shrink-0" />
                    {item}
                  </li>
                ))}
              </ul>
            </div>
          </div>
        </div>
      </div>

      {/* Comparison Table */}
      <div id="comparison" className="py-20 px-6 bg-white/50 backdrop-blur-sm scroll-mt-14">
        <div className="max-w-5xl mx-auto">
          <p className="text-sm font-semibold text-center text-blue-600 uppercase tracking-widest mb-3">Comparison</p>
          <h2 className="text-4xl font-bold text-center text-gray-900 mb-4">
            Standard Autopilot vs. Monitored Autopilot
          </h2>
          <p className="text-center text-gray-500 mb-12 max-w-2xl mx-auto">
            See what you're missing without Autopilot Monitor — and what you gain the moment you deploy it.
          </p>

          {/* Header */}
          <div className="grid grid-cols-[1fr_1fr_1fr] gap-0 mb-1">
            <div />
            <div className="bg-gradient-to-br from-blue-600 to-indigo-600 text-white text-center py-4 px-6 rounded-t-2xl mx-1">
              <div className="font-bold text-lg">Autopilot Monitor</div>
              <div className="text-blue-200 text-sm mt-0.5">Fully Monitored</div>
            </div>
            <div className="bg-gray-100 text-center py-4 px-6 rounded-t-2xl mx-1">
              <div className="font-semibold text-gray-700 text-lg">Standard Autopilot</div>
              <div className="text-gray-400 text-sm mt-0.5">Out of the Box</div>
            </div>
          </div>

          {/* Rows */}
          {[
            {
              label: "Deployment Visibility",
              monitor: "Real-time phase tracking with live push updates",
              standard: "None — black box until it finishes or fails",
            },
            {
              label: "Download Progress",
              monitor: "Per-app download speed, bytes transferred, % complete",
              standard: "No visibility into what's downloading or how long it takes",
            },
            {
              label: "User-Facing Progress Page",
              monitor: "Progress view with live app status & download info",
              standard: "Generic ESP screen — no details for the end user",
            },
            {
              label: "Fleet Health Dashboard",
              monitor: "Success rates, failure trends, avg. duration across all devices",
              standard: "Limited manual report extraction from Intune",
            },
            {
              label: "Analyze Rules",
              monitor: "Built-in + fully customizable rules for automated issue detection",
              standard: "Manual log review required after every failed deployment",
            },
            {
              label: "Extended Data Gathering",
              monitor: "Custom gather rules to capture registry, files, or WMI on any event",
              standard: "No automated data collection during enrollment",
            },
            {
              label: "Geo & Network Context",
              monitor: "Device location, and network info captured at enrollment start",
              standard: "No location or network context in deployment records",
            },
            {
              label: "Performance Monitoring",
              monitor: "CPU, memory, disk, and network snapshots during deployment",
              standard: "Not captured — no way to detect resource bottlenecks",
            },
            {
              label: "Troubleshooting Speed",
              monitor: "Drill into per-event timeline, IME log patterns, and analyze results",
              standard: "Manual IME log hunting — slow and error-prone",
            },
          ].map((row, i) => (
            <div
              key={row.label}
              className={`grid grid-cols-[1fr_1fr_1fr] gap-0 border-b border-gray-100 ${i % 2 === 0 ? "bg-white" : "bg-gray-50/60"}`}
            >
              <div className="py-4 px-5 font-semibold text-gray-800 text-sm flex items-center">{row.label}</div>
              <div className="py-4 px-5 mx-1 bg-blue-50/50 text-sm text-blue-900 flex items-start gap-2">
                <svg className="w-4 h-4 text-blue-500 mt-0.5 shrink-0" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2.5}>
                  <path strokeLinecap="round" strokeLinejoin="round" d="M5 13l4 4L19 7" />
                </svg>
                {row.monitor}
              </div>
              <div className="py-4 px-5 mx-1 text-sm text-gray-400 flex items-start gap-2">
                <svg className="w-4 h-4 text-gray-300 mt-0.5 shrink-0" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2.5}>
                  <path strokeLinecap="round" strokeLinejoin="round" d="M6 18L18 6M6 6l12 12" />
                </svg>
                {row.standard}
              </div>
            </div>
          ))}

          {/* Bottom cap */}
          <div className="grid grid-cols-[1fr_1fr_1fr] gap-0">
            <div />
            <div className="bg-gradient-to-br from-blue-600 to-indigo-600 rounded-b-2xl mx-1 py-3 text-center text-blue-100 text-xs font-medium">
              Full observability from day one
            </div>
            <div className="bg-gray-100 rounded-b-2xl mx-1 py-3 text-center text-gray-400 text-xs">
              Limited to what Intune reports after the fact
            </div>
          </div>
        </div>
      </div>

      {/* CTA Section */}
      <div className="py-20 px-6">
        <div className="max-w-4xl mx-auto text-center">
          <h2 className="text-4xl font-bold text-gray-900 mb-6">
            Ready to transform your Autopilot monitoring?
          </h2>
          <p className="text-xl text-gray-600 mb-8">
            Join organizations using Autopilot Monitor to react faster and monitor more reliably.
          </p>
          <LoginButton
            className="px-8 py-4 bg-gradient-to-r from-blue-600 to-indigo-600 text-white rounded-lg font-semibold text-lg shadow-xl hover:shadow-2xl transform hover:-translate-y-0.5 transition-all"
          >
            Start Monitoring Now
          </LoginButton>
        </div>
      </div>

      {/* Footer */}
      <footer className="border-t border-gray-200/80 bg-white/60 backdrop-blur-sm">
        <div className="max-w-7xl mx-auto px-6 py-10">
          <div className="flex flex-col md:flex-row md:items-start gap-10">
            {/* Brand — left, fixed width with extra right margin */}
            <div className="shrink-0 md:w-52 md:mr-32">
              <div className="flex items-center space-x-2.5 mb-3">
                <div className="w-7 h-7 bg-gradient-to-br from-blue-600 to-indigo-600 rounded-lg flex items-center justify-center">
                  <svg className="w-4 h-4 text-white" viewBox="0 0 24 24" fill="none">
                    <rect x="5.0" y="12.2" width="2.8" height="7.8" rx="0.9" fill="currentColor" />
                    <rect x="10.6" y="10.9" width="2.8" height="9.1" rx="0.9" fill="currentColor" />
                    <rect x="16.2" y="8.6" width="2.8" height="11.4" rx="0.9" fill="currentColor" />
                    <path d="M4.4 8.9L8.6 6.8L12.0 7.4L15.4 5.5L18.8 4.9" stroke="currentColor" strokeWidth="1" strokeLinecap="round" strokeLinejoin="round" />
                    <path d="M17.8 4.2L19.1 4.9L17.9 5.9" stroke="currentColor" strokeWidth="1" strokeLinecap="round" strokeLinejoin="round" />
                  </svg>
                </div>
                <span className="text-sm font-bold text-gray-900">Autopilot Monitor</span>
              </div>
              <p className="text-xs text-gray-500 leading-relaxed mb-4">
                Real-time monitoring and analytics for Windows enrollments.
              </p>
              <div className="flex items-center gap-3">
                <a href="https://www.linkedin.com/in/oliver-kieselbach/" target="_blank" rel="noopener noreferrer" className="text-gray-400 hover:text-blue-600 transition-colors" title="LinkedIn">
                  <svg className="w-4 h-4" fill="currentColor" viewBox="0 0 24 24">
                    <path d="M20.447 20.452h-3.554v-5.569c0-1.328-.027-3.037-1.852-3.037-1.853 0-2.136 1.445-2.136 2.939v5.667H9.351V9h3.414v1.561h.046c.477-.9 1.637-1.85 3.37-1.85 3.601 0 4.267 2.37 4.267 5.455v6.286zM5.337 7.433a2.062 2.062 0 01-2.063-2.065 2.064 2.064 0 112.063 2.065zm1.782 13.019H3.555V9h3.564v11.452zM22.225 0H1.771C.792 0 0 .774 0 1.729v20.542C0 23.227.792 24 1.771 24h20.451C23.2 24 24 23.227 24 22.271V1.729C24 .774 23.2 0 22.222 0h.003z" />
                  </svg>
                </a>
                <a href="https://github.com/okieselbach/Autopilot-Monitor" target="_blank" rel="noopener noreferrer" className="text-gray-400 hover:text-gray-900 transition-colors" title="GitHub">
                  <svg className="w-4 h-4" fill="currentColor" viewBox="0 0 24 24">
                    <path d="M12 0C5.37 0 0 5.37 0 12c0 5.31 3.435 9.795 8.205 11.385.6.105.825-.255.825-.57 0-.285-.015-1.23-.015-2.235-3.015.555-3.795-.735-4.035-1.41-.135-.345-.72-1.41-1.23-1.695-.42-.225-1.02-.78-.015-.795.945-.015 1.62.87 1.845 1.23 1.08 1.815 2.805 1.305 3.495.99.105-.78.42-1.305.765-1.605-2.67-.3-5.46-1.335-5.46-5.925 0-1.305.465-2.385 1.23-3.225-.12-.3-.54-1.53.12-3.18 0 0 1.005-.315 3.3 1.23.96-.27 1.98-.405 3-.405s2.04.135 3 .405c2.295-1.56 3.3-1.23 3.3-1.23.66 1.65.24 2.88.12 3.18.765.84 1.23 1.905 1.23 3.225 0 4.605-2.805 5.625-5.475 5.925.435.375.81 1.095.81 2.22 0 1.605-.015 2.895-.015 3.3 0 .315.225.69.825.57A12.02 12.02 0 0024 12c0-6.63-5.37-12-12-12z" />
                  </svg>
                </a>
              </div>
            </div>

            {/* Link columns — spread across remaining space */}
            <div className="flex-1 grid grid-cols-2 sm:grid-cols-4 gap-8">
              {/* Product */}
              <div>
                <h4 className="text-[11px] font-semibold text-gray-900 uppercase tracking-wider mb-2.5">Product</h4>
                <ul className="space-y-1.5">
                  <li><a href="#features" className="text-xs text-gray-500 hover:text-blue-600 transition-colors">Features</a></li>
                  <li><a href="#how-it-works" className="text-xs text-gray-500 hover:text-blue-600 transition-colors">How It Works</a></li>
                  <li><a href="#comparison" className="text-xs text-gray-500 hover:text-blue-600 transition-colors">Comparison</a></li>
                </ul>
              </div>

              {/* Resources */}
              <div>
                <h4 className="text-[11px] font-semibold text-gray-900 uppercase tracking-wider mb-2.5">Resources</h4>
                <ul className="space-y-1.5">
                  <li><a href={DOCS_URL} target="_blank" rel="noopener noreferrer" className="text-xs text-gray-500 hover:text-blue-600 transition-colors">Documentation</a></li>
                  <li><a href="https://github.com/okieselbach/Autopilot-Monitor/issues" target="_blank" rel="noopener noreferrer" className="text-xs text-gray-500 hover:text-blue-600 transition-colors">Feedback</a></li>
                  <li><a href="https://github.com/okieselbach/Autopilot-Monitor" target="_blank" rel="noopener noreferrer" className="text-xs text-gray-500 hover:text-blue-600 transition-colors">GitHub</a></li>
                </ul>
              </div>

              {/* About */}
              <div>
                <h4 className="text-[11px] font-semibold text-gray-900 uppercase tracking-wider mb-2.5">About</h4>
                <ul className="space-y-1.5">
                  <li><a href="/about" className="text-xs text-gray-500 hover:text-blue-600 transition-colors">Overview</a></li>
                </ul>
              </div>

              {/* Legal */}
              <div>
                <h4 className="text-[11px] font-semibold text-gray-900 uppercase tracking-wider mb-2.5">Legal</h4>
                <ul className="space-y-1.5">
                  <li><a href="/privacy" className="text-xs text-gray-500 hover:text-blue-600 transition-colors">Privacy Policy</a></li>
                  <li><a href="/terms" className="text-xs text-gray-500 hover:text-blue-600 transition-colors">Terms of Use</a></li>
                  <li><a href="https://www.glueckkanja.com/en/imprint" target="_blank" rel="noopener noreferrer" className="text-xs text-gray-500 hover:text-blue-600 transition-colors">Imprint</a></li>
                </ul>
              </div>
            </div>
          </div>

          {/* Bottom bar */}
          <div className="mt-8 pt-5 border-t border-gray-200 flex flex-col sm:flex-row items-center justify-between gap-2">
            <p className="text-[11px] text-gray-400">
              &copy; 2026 Autopilot Monitor. Built with ❤️ by Oliver Kieselbach.{" "}
              <span className="inline-block">Hosted on Azure by glueckkanja AG.</span>
            </p>
            <p className="text-[11px] text-gray-400">
              Open source. Star us on{' '}
              <a href="https://github.com/okieselbach/Autopilot-Monitor" target="_blank" rel="noopener noreferrer" className="text-gray-500 hover:text-blue-600 transition-colors">GitHub</a>
            </p>
          </div>
        </div>
      </footer>
    </div>
  );
}
