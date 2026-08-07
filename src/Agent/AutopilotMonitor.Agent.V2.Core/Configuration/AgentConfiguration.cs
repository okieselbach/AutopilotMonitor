using System;
using System.Collections.Generic;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2.Core.Configuration
{
    /// <summary>
    /// Agent configuration settings
    /// </summary>
    public class AgentConfiguration
    {
        /// <summary>
        /// Backend API base URL
        /// </summary>
        public string ApiBaseUrl { get; set; }

        /// <summary>
        /// Session identifier for this enrollment
        /// </summary>
        public string SessionId { get; set; }

        /// <summary>
        /// Tenant identifier
        /// </summary>
        public string TenantId { get; set; }

        /// <summary>
        /// Local spool directory path
        /// </summary>
        public string SpoolDirectory { get; set; }

        /// <summary>
        /// Agent log directory path
        /// </summary>
        public string LogDirectory { get; set; }

        /// <summary>
        /// Upload interval in seconds
        /// </summary>
        public int UploadIntervalSeconds { get; set; } = 30;

        /// <summary>
        /// Maximum batch size for events
        /// </summary>
        public int MaxBatchSize { get; set; } = 100;

        /// <summary>
        /// Whether to use client certificate authentication
        /// </summary>
        public bool UseClientCertAuth { get; set; } = true;


        /// <summary>
        /// Log verbosity level: Info (default), Debug, Verbose.
        /// Overridable via remote config.
        /// </summary>
        public AgentLogLevel LogLevel { get; set; } = AgentLogLevel.Info;

        /// <summary>
        /// Maximum retry attempts for failed uploads
        /// </summary>
        public int MaxRetryAttempts { get; set; } = 5;

        /// <summary>
        /// Whether to self-destruct when enrollment completes (remove Scheduled Task and all files).
        /// This is the only cleanup mode — SelfDestruct always removes task + files.
        /// </summary>
        public bool SelfDestructOnComplete { get; set; } = true;

        /// <summary>
        /// Name of the Scheduled Task to remove during self-destruct
        /// </summary>
        public string ScheduledTaskName { get; set; } = Constants.ScheduledTaskName;

        /// <summary>
        /// Whether to reboot the device after enrollment completes
        /// </summary>
        public bool RebootOnComplete { get; set; } = false;

        /// <summary>
        /// Delay in seconds before the reboot is initiated (shutdown.exe /r /t X).
        /// Default: 10 seconds
        /// </summary>
        public int RebootDelaySeconds { get; set; } = 10;

        /// <summary>
        /// When true, the log directory is preserved after self-destruct/cleanup.
        /// All other files (binaries, config, spool, scheduled task) are still removed.
        /// </summary>
        public bool KeepLogFile { get; set; } = false;

        /// <summary>
        /// Whether to enable geo-location detection (queries external IP services)
        /// </summary>
        public bool EnableGeoLocation { get; set; } = true;

        /// <summary>
        /// NTP server address for time check during enrollment.
        /// Default: "time.windows.com"
        /// </summary>
        public string NtpServer { get; set; } = "time.windows.com";

        /// <summary>
        /// Whether to automatically set the device timezone based on IP geolocation.
        /// Requires EnableGeoLocation to be true. Uses tzutil /s to apply.
        /// Default: false
        /// </summary>
        public bool EnableTimezoneAutoSet { get; set; } = false;

        /// <summary>
        /// Optional custom path to IME logs directory for testing.
        /// If set, overrides the default %ProgramData%\Microsoft\IntuneManagementExtension\Logs path.
        /// </summary>
        public string ImeLogPathOverride { get; set; }

        /// <summary>
        /// Optional path to write every IME log line that matched a pattern (for debugging).
        /// If empty, no match log is written. Example: %ProgramData%\AutopilotMonitor\Logs\ime-matches.log
        /// </summary>
        public string ImeMatchLogPath { get; set; }

        /// <summary>
        /// Optional path to write a gather-rule evaluation trace (for debugging).
        /// If empty, no trace is written. Set from remote config (EnableGatherRuleDebugLog)
        /// or the --gather-debug-log CLI override.
        /// </summary>
        public string GatherRuleDebugLogPath { get; set; }

        /// <summary>
        /// Path to directory with real IME log files for log replay.
        /// When set, the agent replays these logs with time compression instead of reading live logs.
        /// </summary>
        public string ReplayLogDir { get; set; }

        /// <summary>
        /// Time compression factor for log replay.
        /// Higher values = faster replay. Default: 50 (e.g., 50 minutes of real logs = 1 minute of replay).
        /// </summary>
        public double ReplaySpeedFactor { get; set; } = 50;

        /// <summary>
        /// Wait time in seconds after ESP exit before marking Hello as skipped.
        /// When ESP exits, we wait this duration for Hello wizard to start (event 62404).
        /// Default: 30 seconds (reasonable for systems under load).
        /// If Hello wizard starts within this window, we continue waiting for Hello completion (300/301).
        /// </summary>
        public int HelloWaitTimeoutSeconds { get; set; } = 30;

        /// <summary>
        /// Maximum consecutive authentication failures (401/403) before the agent shuts down.
        /// Prevents endless retry traffic when the device is not authorized.
        /// 0 = disabled (retry forever). Default: 5.
        /// </summary>
        public int MaxAuthFailures { get; set; } = 5;

        /// <summary>
        /// Maximum time in minutes the agent keeps retrying after the first auth failure.
        /// 0 = disabled (no time limit, only MaxAuthFailures applies). Default: 0.
        /// </summary>
        public int AuthFailureTimeoutMinutes { get; set; } = 0;

        /// <summary>
        /// Maximum agent lifetime in minutes. Safety net to prevent zombie agents.
        /// The agent emits enrollment_failed with failureType "agent_timeout" when this expires.
        /// 0 = disabled (no lifetime limit). Default: 360 (6 hours).
        /// </summary>
        public int AgentMaxLifetimeMinutes { get; set; } = 360;

        /// <summary>
        /// Absolute maximum session age in hours across all agent restarts.
        /// Emergency break: if the session has been alive longer than this, the agent
        /// forces cleanup and self-destructs regardless of enrollment state.
        /// Prevents zombie agents caused by unrecoverable logic errors.
        /// Respects WhiteGlove scenarios (timer resets on Part 2 resume).
        /// Default: 48 hours.
        /// </summary>
        public int AbsoluteMaxSessionHours { get; set; } = 48;

        /// <summary>
        /// Bootstrap token for pre-MDM auth during OOBE.
        /// Embedded by the OOBE bootstrapper script via --bootstrap-token CLI arg.
        /// When set, sent as X-Bootstrap-Token header instead of cert auth.
        /// </summary>
        public string BootstrapToken { get; set; }

        /// <summary>
        /// Whether to use bootstrap token auth (true until MDM cert becomes available).
        /// </summary>
        public bool UseBootstrapTokenAuth { get; set; }

        /// <summary>
        /// Whether to show a visual enrollment summary dialog to the end user.
        /// </summary>
        public bool ShowEnrollmentSummary { get; set; } = false;

        /// <summary>
        /// Auto-close timeout in seconds for the enrollment summary dialog.
        /// 0 = no auto-close. Default: 60
        /// </summary>
        public int EnrollmentSummaryTimeoutSeconds { get; set; } = 60;

        /// <summary>
        /// Optional URL to a branding image for the enrollment summary dialog banner.
        /// </summary>
        public string EnrollmentSummaryBrandingImageUrl { get; set; }

        /// <summary>
        /// Maximum time in seconds the agent retries launching the enrollment summary dialog
        /// when the user's desktop is locked by a credential UI (e.g. Windows Hello).
        /// 0 = no retry (single attempt). Default: 120.
        /// </summary>
        public int EnrollmentSummaryLaunchRetrySeconds { get; set; } = 120;

        /// <summary>
        /// Whether diagnostics upload is configured for this tenant.
        /// When true, the agent requests a short-lived upload URL from the backend just before uploading.
        /// The SAS URL itself is never stored in config — it is fetched on-demand and used in memory only.
        /// </summary>
        public bool DiagnosticsUploadEnabled { get; set; } = false;

        /// <summary>
        /// When to upload diagnostics packages: "Off", "Always", "OnFailure".
        /// Default: "Off"
        /// </summary>
        public string DiagnosticsUploadMode { get; set; } = "Off";

        /// <summary>
        /// Merged list of log paths/wildcards to include in the diagnostics ZIP package.
        /// Received from the backend config (global + tenant-specific).
        /// Each entry is validated against DiagnosticsPathGuards before use.
        /// </summary>
        public List<DiagnosticsLogPath> DiagnosticsLogPaths { get; set; } = new List<DiagnosticsLogPath>();

        /// <summary>
        /// Whether the agent should send Trace-severity events to the backend.
        /// Controlled per tenant via remote config. Default: true (on in preview).
        /// </summary>
        public bool SendTraceEvents { get; set; } = true;

        /// <summary>
        /// When true, agent guardrails are relaxed: all registry, WMI, and command targets are allowed.
        /// File/diagnostics paths allow everything except C:\Users.
        /// Default: false.
        /// </summary>
        public bool UnrestrictedMode { get; set; } = false;

        /// <summary>
        /// Sanitized command-line arguments for the agent_started event.
        /// Secrets (e.g. bootstrap token) are redacted before assignment.
        /// </summary>
        public string CommandLineArgs { get; set; }

        /// <summary>
        /// When true, the agent waits for an MDM certificate to appear before starting.
        /// Used when deploying the agent before Intune enrollment completes.
        /// The agent polls the certificate store every 5 seconds until a valid cert is found.
        /// </summary>
        public bool AwaitEnrollment { get; set; } = false;

        /// <summary>
        /// Maximum time in minutes to wait for the MDM certificate in await-enrollment mode.
        /// 0 = wait indefinitely. Default: 480 (8 hours).
        /// </summary>
        public int AwaitEnrollmentTimeoutMinutes { get; set; } = 480;

        /// <summary>
        /// Maximum time in seconds the agent waits for a TenantId to become resolvable
        /// before bailing. When &gt; 0 and the initial registry probe finds no TenantId,
        /// the agent registers a RegistryWatcher on the relevant Enrollments and
        /// CloudDomainJoin keys and re-probes on every change until either resolution
        /// or this timeout. 0 = no wait, fast-fail on miss (legacy behaviour).
        /// <para>
        /// Set via the CLI flag <c>--tenant-id-wait &lt;sec&gt;</c> or a persisted
        /// <c>bootstrap-config.json</c> value; when neither supplies a value the agent
        /// applies its own default of 600 s in BuildAgentConfiguration. The 0 default
        /// here is only the POCO baseline, not the effective runtime default.
        /// </para>
        /// </summary>
        public int TenantIdWaitSeconds { get; set; } = 0;

        /// <summary>
        /// Validates the configuration
        /// </summary>
        public bool IsValid()
        {
            return !string.IsNullOrEmpty(ApiBaseUrl) &&
                   !string.IsNullOrEmpty(SessionId) &&
                   !string.IsNullOrEmpty(TenantId);
        }
    }
}
