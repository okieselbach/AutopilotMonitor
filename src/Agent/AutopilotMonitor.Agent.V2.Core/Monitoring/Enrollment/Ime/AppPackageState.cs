using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime
{
    /// <summary>
    /// Installation state of an app package during enrollment.
    /// Order matters: states are compared numerically for upgrade-only transitions.
    /// </summary>
    public enum AppInstallationState
    {
        Unknown = 0,
        NotInstalled = 1,
        InProgress = 2,
        Downloading = 3,
        Installing = 4,
        Installed = 5,
        Skipped = 6,
        Postponed = 7,
        Error = 8
    }

    /// <summary>
    /// How the app should run
    /// </summary>
    public enum AppRunAs
    {
        Unknown = -1,
        User = 0,
        System = 1
    }

    /// <summary>
    /// App install intent from Intune policy
    /// </summary>
    public enum AppIntent
    {
        Unknown = -1,
        NotTargeted = 0,
        Available = 1,
        Install = 3,
        Uninstall = 4
    }

    /// <summary>
    /// How the app is targeted
    /// </summary>
    [Flags]
    public enum AppTargeted
    {
        Unknown = 128,
        Dependency = 0,
        User = 1,
        Device = 2
    }

    /// <summary>
    /// Win32 app state values from IME
    /// </summary>
    public enum Win32AppState
    {
        Unknown = 0,
        NotInstalled = 1,
        InProgress = 2,
        Completed = 3,
        Error = 4
    }

    /// <summary>
    /// Represents the state of a single app package during enrollment.
    /// Adapted from EspOverlay PackageState with state transition protection.
    /// </summary>
    public class AppPackageState : IComparable<AppPackageState>
    {
        public static bool SortErrorsToTop = false;

        public string Id { get; }
        public int ListPos { get; }
        public string Name { get; private set; }
        public AppRunAs RunAs { get; private set; } = AppRunAs.Unknown;
        public AppIntent Intent { get; private set; } = AppIntent.Unknown;
        public AppTargeted Targeted { get; private set; } = AppTargeted.Unknown;
        public HashSet<string> DependsOn { get; private set; } = new HashSet<string>();
        public AppInstallationState InstallationState { get; private set; } = AppInstallationState.Unknown;
        public long InstallationStateLastChangedTicks { get; private set; } = 0;
        public bool DownloadingOrInstallingSeen { get; private set; } = false;
        // PR5: surface the inverse-detection auto-downgrade (Installed -> Skipped without prior
        // download/install seen) so the adapter / UI can explain WHY an app appears as Skipped.
        // Set once when the auto-downgrade fires. Read-only outside of UpdateState.
        public bool WasAutoDowngradedToSkipped { get; private set; }
        public int? ProgressPercent { get; private set; } = null;
        public long BytesDownloaded { get; private set; } = 0;
        public long BytesTotal { get; private set; } = 0;
        public string ErrorPatternId { get; private set; }
        public string ErrorDetail { get; private set; }
        public string ErrorCode { get; private set; }

        // Installer result codes (captured from lpExitCode / hResultFromWin32 log lines)
        public string ExitCode { get; private set; }
        public string HResultFromWin32 { get; private set; }
        /// <summary>
        /// Admin-defined return-code class of the last installer exit code: Success, SoftReboot,
        /// HardReboot, Retry or Failed (IME's AppReturnCodeType, captured from the
        /// "lpExitCode is defined as X" line by IME-EXITCODE-CLASS). Last value wins, so a Retry
        /// loop that ends in Success reports Success.
        /// </summary>
        public string ExitCodeClass { get; private set; }

        // App metadata (captured from IME log patterns — used by App Dashboard / reports)
        /// <summary>Product version detected after install (e.g. "11.2.1787.0"). From ReportingManager DetectedIdentityVersion.</summary>
        public string AppVersion { get; private set; }
        /// <summary>App installer type: "Win32", "MSI", "WinGet", "Store", "LOB". Derived from [WinGetApp]/MSI markers.</summary>
        public string AppType { get; private set; }
        /// <summary>Install attempt number (1 = first try). Increments on each "Execute retry N" line per check-in cycle.</summary>
        public int AttemptNumber { get; private set; }
        /// <summary>Detection rule result: "Detected" or "NotDetected". From DetectionActionHandler log lines.</summary>
        public string DetectionResult { get; private set; }

        // Delivery Optimization telemetry (populated from [DO TEL] log entries OR Get-DeliveryOptimizationStatus poll)
        public long DoFileSize { get; private set; }
        public long DoTotalBytesDownloaded { get; private set; }
        public long DoBytesFromPeers { get; private set; }
        public int DoPercentPeerCaching { get; private set; }
        public long DoBytesFromLanPeers { get; private set; }
        public long DoBytesFromGroupPeers { get; private set; }
        public long DoBytesFromInternetPeers { get; private set; }
        // Microsoft DO splits "peers" into four buckets; LinkLocal (same subnet) is technically separate
        // from LAN but for UX we typically merge them under "local network" — kept as its own field here
        // so future tooling can disambiguate.
        public long DoBytesFromLinkLocalPeers { get; private set; }
        // Microsoft Connected Cache (MCC) bytes — counted separately from BytesFromPeers by DO; on
        // machines with an MCC server BytesFromHttp typically equals BytesFromCacheServer. Tracking
        // it explicitly so the UI can show "From Connected Cache" instead of misleading "From HTTP".
        public long DoBytesFromCacheServer { get; private set; }
        // CacheHost URI/IP of the MCC node that served the bytes. Only set when DoBytesFromCacheServer > 0.
        public string DoCacheHost { get; private set; }
        public int DoDownloadMode { get; private set; } = -1;
        public string DoDownloadDuration { get; private set; }
        public long DoBytesFromHttp { get; private set; }
        public bool HasDoTelemetry { get; private set; }

        private static readonly Dictionary<Win32AppState, AppInstallationState> Win32StateMap =
            new Dictionary<Win32AppState, AppInstallationState>
            {
                { Win32AppState.Unknown, AppInstallationState.Unknown },
                { Win32AppState.InProgress, AppInstallationState.InProgress },
                { Win32AppState.Completed, AppInstallationState.Installed },
                { Win32AppState.Error, AppInstallationState.Error }
            };

        private static readonly Dictionary<AppInstallationState, int> SortOrderOverrides =
            new Dictionary<AppInstallationState, int>
            {
                { AppInstallationState.Skipped, (int)AppInstallationState.Installed },
                { AppInstallationState.Postponed, (int)AppInstallationState.Installed },
                { AppInstallationState.Error, (int)AppInstallationState.Installed }
            };

        public AppPackageState(string id, int listPos)
        {
            Id = id;
            ListPos = listPos;
        }

        /// <summary>
        /// Restores an AppPackageState from persisted data (used on agent restart).
        /// </summary>
        internal static AppPackageState Restore(
            string id, int listPos, string name,
            AppRunAs runAs, AppIntent intent, AppTargeted targeted,
            HashSet<string> dependsOn,
            AppInstallationState installationState, bool downloadingOrInstallingSeen,
            int? progressPercent, long bytesDownloaded, long bytesTotal,
            string errorPatternId = null, string errorDetail = null, string errorCode = null,
            string exitCode = null, string hresultFromWin32 = null,
            string exitCodeClass = null,
            long doFileSize = 0, long doTotalBytesDownloaded = 0,
            long doBytesFromPeers = 0, int doPercentPeerCaching = 0,
            long doBytesFromLanPeers = 0, long doBytesFromGroupPeers = 0,
            long doBytesFromInternetPeers = 0, int doDownloadMode = -1,
            string doDownloadDuration = null, long doBytesFromHttp = 0,
            bool hasDoTelemetry = false,
            long doBytesFromLinkLocalPeers = 0, long doBytesFromCacheServer = 0,
            string doCacheHost = null,
            string appVersion = null, string appType = null,
            int attemptNumber = 0, string detectionResult = null)
        {
            var pkg = new AppPackageState(id, listPos)
            {
                Name = name,
                RunAs = runAs,
                Intent = intent,
                Targeted = targeted,
                DependsOn = dependsOn ?? new HashSet<string>(),
                InstallationState = installationState,
                DownloadingOrInstallingSeen = downloadingOrInstallingSeen,
                ProgressPercent = progressPercent,
                BytesDownloaded = bytesDownloaded,
                BytesTotal = bytesTotal,
                ErrorPatternId = errorPatternId,
                ErrorDetail = errorDetail,
                ErrorCode = errorCode,
                ExitCode = exitCode,
                HResultFromWin32 = hresultFromWin32,
                ExitCodeClass = exitCodeClass,
                InstallationStateLastChangedTicks = Stopwatch.GetTimestamp(),
                DoFileSize = doFileSize,
                DoTotalBytesDownloaded = doTotalBytesDownloaded,
                DoBytesFromPeers = doBytesFromPeers,
                DoPercentPeerCaching = doPercentPeerCaching,
                DoBytesFromLanPeers = doBytesFromLanPeers,
                DoBytesFromGroupPeers = doBytesFromGroupPeers,
                DoBytesFromInternetPeers = doBytesFromInternetPeers,
                DoBytesFromLinkLocalPeers = doBytesFromLinkLocalPeers,
                DoBytesFromCacheServer = doBytesFromCacheServer,
                DoCacheHost = doCacheHost,
                DoDownloadMode = doDownloadMode,
                DoDownloadDuration = doDownloadDuration,
                DoBytesFromHttp = doBytesFromHttp,
                HasDoTelemetry = hasDoTelemetry,
                AppVersion = appVersion,
                AppType = appType,
                AttemptNumber = attemptNumber,
                DetectionResult = detectionResult
            };
            return pkg;
        }

        /// <summary>
        /// Updates the app name. Only updates if the new name is not a truncated version of the current name.
        /// </summary>
        public bool UpdateName(string newName)
        {
            if (string.IsNullOrEmpty(newName) || string.Equals(Name, newName))
                return false;

            // Avoid overwriting with a truncated version
            if (Name != null && newName.Length > 0 && Name.StartsWith(newName))
                return false;

            Name = newName;
            return true;
        }

        /// <summary>
        /// Updates RunAs property
        /// </summary>
        public bool UpdateRunAs(AppRunAs newRunAs)
        {
            if (RunAs == newRunAs) return false;
            RunAs = newRunAs;
            return true;
        }

        /// <summary>
        /// Updates Intent property
        /// </summary>
        public bool UpdateIntent(AppIntent newIntent)
        {
            if (Intent == newIntent) return false;
            Intent = newIntent;
            return true;
        }

        /// <summary>
        /// Updates Targeted property
        /// </summary>
        public bool UpdateTargeted(AppTargeted newTargeted)
        {
            if (Targeted == newTargeted) return false;
            Targeted = newTargeted;
            return true;
        }

        /// <summary>
        /// Updates DependsOn set
        /// </summary>
        public bool UpdateDependsOn(HashSet<string> newDependsOn)
        {
            if (newDependsOn == null) newDependsOn = new HashSet<string>();
            if (DependsOn.SetEquals(newDependsOn)) return false;
            DependsOn = newDependsOn;
            return true;
        }

        /// <summary>
        /// Sets the error context for the app (called when an error pattern is matched).
        /// </summary>
        public void SetErrorContext(string patternId, string detail, string errorCode = null)
        {
            ErrorPatternId = patternId;
            ErrorDetail = detail;
            if (!string.IsNullOrEmpty(errorCode))
                ErrorCode = errorCode;
        }

        /// <summary>
        /// Captures the installer exit code (lpExitCode) for the app.
        /// </summary>
        public void UpdateExitCode(string code)
        {
            ExitCode = code;
        }

        /// <summary>
        /// Captures the Win32 HRESULT (hResultFromWin32) for the app.
        /// </summary>
        public void UpdateHResult(string code)
        {
            HResultFromWin32 = code;
        }

        /// <summary>
        /// Captures the return-code class (Success/SoftReboot/HardReboot/Retry/Failed) IME assigned
        /// to the last exit code. Last value wins; empty values are ignored.
        /// </summary>
        public bool UpdateExitCodeClass(string exitCodeClass)
        {
            if (string.IsNullOrWhiteSpace(exitCodeClass)) return false;
            var trimmed = exitCodeClass.Trim();
            if (ExitCodeClass == trimmed) return false;
            ExitCodeClass = trimmed;
            return true;
        }

        /// <summary>
        /// Updates the product version detected for this app (e.g. "11.2.1787.0").
        /// Only applies non-empty values so a later detection without version cannot wipe it.
        /// </summary>
        public bool UpdateAppVersion(string version)
        {
            if (string.IsNullOrWhiteSpace(version)) return false;
            if (AppVersion == version) return false;
            AppVersion = version.Trim();
            return true;
        }

        /// <summary>
        /// Updates the app installer type. Only sets when current is empty OR
        /// when the incoming value is more specific than the existing one
        /// (e.g. "WinGet"/"MSI" override the "Win32" default).
        /// </summary>
        public bool UpdateAppType(string type)
        {
            if (string.IsNullOrWhiteSpace(type)) return false;
            if (AppType == type) return false;
            // Do not let a generic "Win32" downgrade a previously determined "WinGet"/"MSI".
            if (!string.IsNullOrEmpty(AppType) && type == "Win32"
                && (AppType == "WinGet" || AppType == "MSI" || AppType == "Store" || AppType == "LOB"))
                return false;
            AppType = type;
            return true;
        }

        /// <summary>
        /// Updates the install attempt number. Keeps the highest observed value
        /// so it is monotonic across log batches.
        /// </summary>
        public bool UpdateAttemptNumber(int attempt)
        {
            if (attempt <= AttemptNumber) return false;
            AttemptNumber = attempt;
            return true;
        }

        /// <summary>
        /// Updates the detection rule result ("Detected" / "NotDetected").
        /// Applies any non-empty value so the latest detection wins.
        /// </summary>
        public bool UpdateDetectionResult(string result)
        {
            if (string.IsNullOrWhiteSpace(result)) return false;
            if (DetectionResult == result) return false;
            DetectionResult = result.Trim();
            return true;
        }

        /// <summary>
        /// Updates Delivery Optimization telemetry data for this app package.
        /// Called when a [DO TEL] log entry is matched (IME path) or when
        /// Get-DeliveryOptimizationStatus reports a completed download (OS poll path).
        /// LinkLocal/CacheServer/CacheHost default to 0/null on older agents or when
        /// the source omits them — caller must pass 0/null in that case.
        /// </summary>
        public void UpdateDoTelemetry(long fileSize, long totalBytesDownloaded,
            long bytesFromPeers, int percentPeerCaching,
            long bytesFromLanPeers, long bytesFromGroupPeers, long bytesFromInternetPeers,
            int downloadMode, string downloadDuration, long bytesFromHttp,
            long bytesFromLinkLocalPeers = 0, long bytesFromCacheServer = 0, string cacheHost = null)
        {
            DoFileSize = fileSize;
            DoTotalBytesDownloaded = totalBytesDownloaded;
            DoBytesFromPeers = bytesFromPeers;
            DoPercentPeerCaching = percentPeerCaching;
            DoBytesFromLanPeers = bytesFromLanPeers;
            DoBytesFromGroupPeers = bytesFromGroupPeers;
            DoBytesFromInternetPeers = bytesFromInternetPeers;
            DoBytesFromLinkLocalPeers = bytesFromLinkLocalPeers;
            DoBytesFromCacheServer = bytesFromCacheServer;
            DoCacheHost = string.IsNullOrEmpty(cacheHost) ? null : cacheHost;
            DoDownloadMode = downloadMode;
            DoDownloadDuration = downloadDuration;
            DoBytesFromHttp = bytesFromHttp;
            HasDoTelemetry = true;
        }

        /// <summary>
        /// Updates the installation state with transition protection.
        /// Returns true if the state actually changed.
        /// </summary>
        public bool UpdateState(AppInstallationState newState, int? newProgressPercent = null, bool upgradeOnly = false, long bytesDownloaded = 0, long bytesTotal = 0)
        {
            // upgradeOnly: only allow "higher" states (used by Win32AppState mapping)
            if (upgradeOnly && newState < InstallationState)
                return false;

            // Cannot downgrade from Installed to Skipped or Postponed
            if (InstallationState == AppInstallationState.Installed &&
                (newState == AppInstallationState.Skipped || newState == AppInstallationState.Postponed))
                return false;

            // Postponed cannot go back to Downloading or lower active states
            if (InstallationState == AppInstallationState.Postponed && newState <= AppInstallationState.Downloading)
                return false;

            // If "Installed" without ever seeing download/install -> auto-downgrade to Skipped
            // This handles apps with "inverse" detection rules (e.g., uninstall packages marked as
            // installed when old software is NOT detected). PR5: also flag the auto-downgrade so
            // the caller can log "App X reclassified Installed -> Skipped (inverse detection)".
            if (newState == AppInstallationState.Installed && !DownloadingOrInstallingSeen)
            {
                newState = AppInstallationState.Skipped;
                WasAutoDowngradedToSkipped = true;
            }

            // Compute the effective progress percent that will be applied
            var effectiveProgressPercent = (newState == AppInstallationState.Installed && newProgressPercent == null) ? 100 : newProgressPercent;
            // Compute effective bytes: only consider new values if non-zero, otherwise keep current
            var effectiveBytesDownloaded = (bytesDownloaded > 0 || bytesTotal > 0) ? bytesDownloaded : BytesDownloaded;
            var effectiveBytesTotal = (bytesDownloaded > 0 || bytesTotal > 0) ? bytesTotal : BytesTotal;
            // After WinGet completion fix: if Installed and total > 0 and downloaded < total, downloaded becomes total
            if (newState == AppInstallationState.Installed && effectiveBytesTotal > 0 && effectiveBytesDownloaded < effectiveBytesTotal)
                effectiveBytesDownloaded = effectiveBytesTotal;

            // Skip if no actual change
            if (newState == InstallationState && effectiveProgressPercent == ProgressPercent && effectiveBytesDownloaded == BytesDownloaded && effectiveBytesTotal == BytesTotal)
                return false;

            var oldState = InstallationState;
            InstallationState = newState;
            InstallationStateLastChangedTicks = Stopwatch.GetTimestamp();
            DownloadingOrInstallingSeen |= (InstallationState >= AppInstallationState.Downloading &&
                                            InstallationState <= AppInstallationState.Installing);
            ProgressPercent = effectiveProgressPercent;
            BytesDownloaded = effectiveBytesDownloaded;
            BytesTotal = effectiveBytesTotal;

            return true;
        }

        /// <summary>
        /// Updates state from a Win32AppState string or integer string (e.g., "2" or "InProgress").
        /// Uses upgrade-only mode to prevent "InProgress" from destroying more detailed states.
        /// </summary>
        public bool UpdateStateFromWin32AppState(string win32AppStateStringOrIntString)
        {
            Win32AppState win32State;
            if (Enum.TryParse(win32AppStateStringOrIntString, true, out win32State))
                return UpdateStateFromWin32AppState(win32State);
            return false;
        }

        /// <summary>
        /// Updates state from Win32AppState enum value.
        /// </summary>
        public bool UpdateStateFromWin32AppState(Win32AppState win32State)
        {
            AppInstallationState newState;
            if (Win32StateMap.TryGetValue(win32State, out newState))
                return UpdateState(newState, upgradeOnly: true);
            return false;
        }

        /// <summary>
        /// Whether this app requires installation (Install or Uninstall intent)
        /// </summary>
        public bool IsRequired => Intent == AppIntent.Install || Intent == AppIntent.Uninstall;

        /// <summary>
        /// Whether the app is currently being processed (InProgress, Downloading, or Installing)
        /// </summary>
        public bool IsActive => InstallationState >= AppInstallationState.InProgress &&
                                InstallationState <= AppInstallationState.Installing;

        /// <summary>
        /// Whether the app has reached a terminal state (Installed, Skipped, Postponed, Error)
        /// </summary>
        public bool IsCompleted => InstallationState >= AppInstallationState.Installed;

        /// <summary>
        /// Whether the app is in an error state
        /// </summary>
        public bool IsError => InstallationState == AppInstallationState.Error;

        /// <summary>
        /// Comparison for sorting: active apps first, then by state change time, then by list position.
        /// </summary>
        public int CompareTo(AppPackageState other)
        {
            // First: descending on sort order (active apps at top)
            var result = -(SortOrder.CompareTo(other.SortOrder));
            if (result == 0)
            {
                // Then: by installation state last changed
                result = InstallationStateLastChangedTicks.CompareTo(other.InstallationStateLastChangedTicks);
            }
            if (result == 0)
            {
                // Then: by original position in policy list
                result = ListPos.CompareTo(other.ListPos);
            }
            return result;
        }

        private int SortOrder
        {
            get
            {
                if (SortErrorsToTop && InstallationState == AppInstallationState.Error)
                    return -1;

                int sortOrder;
                if (SortOrderOverrides.TryGetValue(InstallationState, out sortOrder))
                    return sortOrder;

                return (int)InstallationState;
            }
        }

        /// <summary>
        /// Returns a summary dictionary for event data
        /// </summary>
        public Dictionary<string, object> ToEventData()
        {
            var data = new Dictionary<string, object>
            {
                { "appId", Id },
                { "appName", Name ?? Id },
                { "state", InstallationState.ToString() },
                { "intent", Intent.ToString() },
                { "targeted", Targeted.ToString() },
                { "runAs", RunAs.ToString() },
                { "progressPercent", ProgressPercent ?? 0 },
                { "bytesDownloaded", BytesDownloaded },
                { "bytesTotal", BytesTotal },
                { "isError", IsError },
                { "isCompleted", IsCompleted }
            };

            if (!string.IsNullOrEmpty(ErrorPatternId))
                data["errorPatternId"] = ErrorPatternId;
            if (!string.IsNullOrEmpty(ErrorDetail))
                data["errorDetail"] = ErrorDetail;
            if (!string.IsNullOrEmpty(ErrorCode))
                data["errorCode"] = ErrorCode;

            if (!string.IsNullOrEmpty(ExitCode))
                data["exitCode"] = ExitCode;
            if (!string.IsNullOrEmpty(HResultFromWin32))
                data["hresultFromWin32"] = HResultFromWin32;
            if (!string.IsNullOrEmpty(ExitCodeClass))
                data["exitCodeClass"] = ExitCodeClass;

            // App metadata fields (emitted whenever available so ingest can merge them).
            if (!string.IsNullOrEmpty(AppVersion))
                data["appVersion"] = AppVersion;
            if (!string.IsNullOrEmpty(AppType))
                data["appType"] = AppType;
            if (AttemptNumber > 0)
                data["attemptNumber"] = AttemptNumber;
            if (!string.IsNullOrEmpty(DetectionResult))
                data["detectionResult"] = DetectionResult;

            if (HasDoTelemetry)
            {
                data["doFileSize"] = DoFileSize;
                data["doTotalBytesDownloaded"] = DoTotalBytesDownloaded;
                data["doBytesFromPeers"] = DoBytesFromPeers;
                data["doPercentPeerCaching"] = DoPercentPeerCaching;
                data["doBytesFromLanPeers"] = DoBytesFromLanPeers;
                data["doBytesFromGroupPeers"] = DoBytesFromGroupPeers;
                data["doBytesFromInternetPeers"] = DoBytesFromInternetPeers;
                data["doBytesFromLinkLocalPeers"] = DoBytesFromLinkLocalPeers;
                data["doBytesFromCacheServer"] = DoBytesFromCacheServer;
                if (!string.IsNullOrEmpty(DoCacheHost))
                    data["doCacheHost"] = DoCacheHost;
                data["doDownloadMode"] = DoDownloadMode;
                data["doDownloadDuration"] = DoDownloadDuration ?? "";
                data["doBytesFromHttp"] = DoBytesFromHttp;
            }

            return data;
        }
    }
}
