using System;
using System.IO;
using System.Text;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Shared.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AutopilotMonitor.Agent.V2.Core.Security
{
    /// <summary>
    /// Persists the current enrollment SessionId across agent restarts. Plan §4.x M4.5.b.
    /// <para>
    /// V2 uses a single <c>session.id</c> text file in the agent data directory — simpler than
    /// the Legacy <c>SessionPersistence</c> (no <c>.seq</c>, no white-glove marker, no session-age
    /// tracking; those live in the new DecisionState snapshot/journal).
    /// </para>
    /// <para>
    /// Write semantics are atomic (write-to-temp + <see cref="File.Replace(string,string,string)"/>) so a
    /// power-cut mid-write cannot leave a zero-byte file that later reads as "no session" and
    /// trips a fresh SessionId on recovery.
    /// </para>
    /// </summary>
    public sealed class SessionIdPersistence
    {
        private readonly string _sessionFilePath;
        private readonly string _sessionCreatedFilePath;
        private readonly string _whiteGloveMarkerPath;
        private readonly string _registrationFailurePath;
        private readonly object _lockObject = new object();

        // Agent-local keys in registration-failure.json; RegistrationFailureSummary has no such members.
        private const string LocalNetworkFingerprintKey = "NetworkFingerprint";
        private const string LocalDeviceBootKey = "DeviceBootUtc";
        private const string LocalCertThumbprintKey = "CertThumbprint";

        public SessionIdPersistence(string dataDirectory)
        {
            if (string.IsNullOrWhiteSpace(dataDirectory))
                throw new ArgumentNullException(nameof(dataDirectory));

            if (!Directory.Exists(dataDirectory))
                Directory.CreateDirectory(dataDirectory);

            _sessionFilePath = Path.Combine(dataDirectory, "session.id");
            _sessionCreatedFilePath = Path.Combine(dataDirectory, "session.created");
            _whiteGloveMarkerPath = Path.Combine(dataDirectory, "whiteglove.complete");
            _registrationFailurePath = Path.Combine(dataDirectory, "registration-failure.json");
        }

        /// <summary>
        /// Returns the persisted SessionId or creates, writes and returns a fresh one.
        /// Thread-safe — concurrent callers see the same SessionId.
        /// <para>
        /// Also initialises <c>session.created</c> on first creation (and on recovery when it is
        /// missing) so the emergency-break watchdog can compare the session's wall-clock age.
        /// </para>
        /// </summary>
        public string GetOrCreate(AgentLogger logger = null)
        {
            lock (_lockObject)
            {
                try
                {
                    if (File.Exists(_sessionFilePath))
                    {
                        var existing = File.ReadAllText(_sessionFilePath).Trim();
                        if (Guid.TryParse(existing, out _))
                        {
                            if (!File.Exists(_sessionCreatedFilePath))
                            {
                                // Recover session.created when session.id exists but the companion
                                // timestamp was lost (crash between the two writes, upgrade from an
                                // older SessionIdPersistence without session.created).
                                SaveSessionCreatedAtInternal(DateTime.UtcNow);
                                logger?.Info("SessionIdPersistence: initialised missing session.created for existing SessionId.");
                            }
                            logger?.Debug($"SessionIdPersistence: resumed existing SessionId={existing}.");
                            return existing;
                        }
                        logger?.Warning($"SessionIdPersistence: session.id contained non-GUID content — regenerating.");
                    }
                }
                catch (Exception ex)
                {
                    logger?.Warning($"SessionIdPersistence: failed to read session.id ({ex.Message}) — regenerating.");
                }

                var fresh = Guid.NewGuid().ToString();
                WriteAtomic(fresh, logger);
                SaveSessionCreatedAtInternal(DateTime.UtcNow);
                logger?.Info($"SessionIdPersistence: created new SessionId={fresh}.");
                return fresh;
            }
        }

        /// <summary>True when a persisted SessionId is present on disk.</summary>
        public bool SessionExists()
        {
            lock (_lockObject) return File.Exists(_sessionFilePath);
        }

        /// <summary>
        /// Returns the persisted session creation timestamp (UTC) or <c>null</c> when unavailable /
        /// unparseable. Used by the M4.6.α emergency-break watchdog.
        /// </summary>
        public DateTime? LoadSessionCreatedAt()
        {
            lock (_lockObject)
            {
                try
                {
                    if (!File.Exists(_sessionCreatedFilePath)) return null;
                    var raw = File.ReadAllText(_sessionCreatedFilePath).Trim();
                    if (DateTime.TryParse(raw, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
                        return parsed.Kind == DateTimeKind.Utc ? parsed : parsed.ToUniversalTime();
                }
                catch
                {
                    // Fall through — treat corrupt file as missing so the watchdog does not trip on bogus data.
                }
                return null;
            }
        }

        /// <summary>
        /// Persists the session creation timestamp (UTC). Used by the watchdog to establish a
        /// known-good baseline if the file is missing on recovery (see <see cref="GetOrCreate"/>).
        /// </summary>
        public void SaveSessionCreatedAt(DateTime createdAtUtc)
        {
            lock (_lockObject) SaveSessionCreatedAtInternal(createdAtUtc);
        }

        /// <summary>
        /// <c>true</c> when a <c>whiteglove.complete</c> marker was persisted by the Part-1 exit path.
        /// The watchdog treats these sessions as paused (device is powered off between Part 1 and
        /// Part 2) and does NOT trip the emergency break on their age — the age clock restarts on
        /// Part-2 resume.
        /// </summary>
        public bool IsWhiteGloveResume()
        {
            lock (_lockObject) return File.Exists(_whiteGloveMarkerPath);
        }

        /// <summary>
        /// V1 parity (<c>SessionPersistence.SaveWhiteGloveComplete</c>) — persists the
        /// <c>whiteglove.complete</c> marker so the next agent boot recognises this as a
        /// Part-2 resume (<see cref="IsWhiteGloveResume"/>). Without this marker the
        /// emergency-break watchdog would keep counting the session's wall-clock age through
        /// the Part-1/Part-2 power-off window and eventually trip during Part-2 setup.
        /// </summary>
        public void SaveWhiteGloveComplete(AgentLogger logger = null)
        {
            lock (_lockObject)
            {
                try
                {
                    File.WriteAllText(_whiteGloveMarkerPath, DateTime.UtcNow.ToString("O"));
                    logger?.Info("SessionIdPersistence: whiteglove.complete marker saved — Part 2 detection enabled.");
                }
                catch (Exception ex)
                {
                    logger?.Warning($"SessionIdPersistence: SaveWhiteGloveComplete failed: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Clears the <c>whiteglove.complete</c> marker once Part-2 resumes so a subsequent
        /// session does not re-enter the resume code path.
        /// </summary>
        public void ClearWhiteGloveComplete(AgentLogger logger = null)
        {
            lock (_lockObject) TryDelete(_whiteGloveMarkerPath, logger);
        }

        /// <summary>
        /// Folds one failed registration run into <c>registration-failure.json</c>: the run
        /// counters grow (an intercepted run — <c>InterceptedRuns</c> &gt; 0 on
        /// <paramref name="latestRun"/> — also counts toward
        /// <see cref="RegistrationFailureSummary.InterceptedRuns"/>), the first failure time is kept,
        /// everything else describes the latest run. A run that never registers exits and leaves
        /// nothing else behind — the emergency break reads this file so the backend learns why the
        /// session never appeared. <paramref name="networkFingerprint"/>, <paramref name="deviceBootUtc"/>
        /// and <paramref name="certThumbprint"/> (the latest run's network, boot and client
        /// certificate) are stored beside the summary, never in it, so they cannot reach the wire —
        /// the next successful start reports only whether they changed. Written through to disk
        /// (L.12): the process exits right after and the next boot decides on the record.
        /// Returns the merged summary, null when it could not be written. Never throws.
        /// </summary>
        public RegistrationFailureSummary RecordRegistrationFailure(
            RegistrationFailureSummary latestRun, AgentLogger logger = null, string networkFingerprint = null,
            DateTime? deviceBootUtc = null, string certThumbprint = null)
        {
            if (latestRun == null) return null;
            lock (_lockObject)
            {
                try
                {
                    var previous = LoadRegistrationFailureInternal()?.Summary;
                    var merged = new RegistrationFailureSummary
                    {
                        FailedRuns = (previous?.FailedRuns ?? 0) + 1,
                        InterceptedRuns = (previous?.InterceptedRuns ?? 0) + (latestRun.InterceptedRuns > 0 ? 1 : 0),
                        FirstFailedAtUtc = previous?.FirstFailedAtUtc ?? latestRun.LastFailedAtUtc,
                        LastFailedAtUtc = latestRun.LastFailedAtUtc,
                        Outcome = latestRun.Outcome,
                        LastError = latestRun.LastError,
                        NetworkLinkUpAtStart = latestRun.NetworkLinkUpAtStart,
                        NetworkLinkUpAtEnd = latestRun.NetworkLinkUpAtEnd,
                        AttemptWindowSeconds = latestRun.AttemptWindowSeconds,
                        ConfigFetchOutcome = latestRun.ConfigFetchOutcome,
                        EspPhase = latestRun.EspPhase,
                        OobeState = latestRun.OobeState,
                    };
                    WriteRegistrationFailure(merged, networkFingerprint, deviceBootUtc, certThumbprint);
                    logger?.Info($"SessionIdPersistence: recorded failed registration run #{merged.FailedRuns} ({merged.InterceptedRuns} intercepted).");
                    return merged;
                }
                catch (Exception ex)
                {
                    logger?.Warning($"SessionIdPersistence: RecordRegistrationFailure failed: {ex.Message}");
                    return null;
                }
            }
        }

        /// <summary>
        /// The failed-registration record since the last success, or <c>null</c> (none, unreadable).
        /// The summary only — what the emergency break may send.
        /// </summary>
        public RegistrationFailureSummary LoadRegistrationFailure()
        {
            lock (_lockObject) return LoadRegistrationFailureInternal()?.Summary;
        }

        /// <summary>
        /// The failed-registration record plus the agent-local network fingerprint of its latest
        /// run, or <c>null</c> (none, unreadable). For the next successful start's comparison only.
        /// </summary>
        public RegistrationFailureRecord LoadRegistrationFailureRecord()
        {
            lock (_lockObject) return LoadRegistrationFailureInternal();
        }

        /// <summary>Drops the failed-registration record — called once registration succeeds.</summary>
        public void ClearRegistrationFailure(AgentLogger logger = null)
        {
            lock (_lockObject) TryDelete(_registrationFailurePath, logger);
        }

        /// <summary>Deletes the persisted SessionId. Next <see cref="GetOrCreate"/> starts a fresh session.</summary>
        public void Delete(AgentLogger logger = null)
        {
            lock (_lockObject)
            {
                TryDelete(_sessionFilePath, logger);
                TryDelete(_sessionCreatedFilePath, logger);
                TryDelete(_whiteGloveMarkerPath, logger);
                TryDelete(_registrationFailurePath, logger);
            }
        }

        /// <summary>
        /// Replaces the persisted SessionId with a fresh GUID and restarts the session-age clock.
        /// Used when the backend answers register-session with <c>session_owner_mismatch</c>
        /// (SESSION-OWNER-BINDING): the id on disk belongs to a session bound to a different device
        /// identity — the re-enroll-without-wipe shape — so this run must become a new session.
        /// Unlike <see cref="Delete"/> this leaves <c>whiteglove.complete</c> untouched: a Part-2
        /// resume that has to rotate is still a Part-2 resume.
        /// </summary>
        /// <returns>The new SessionId.</returns>
        public string Rotate(AgentLogger logger = null)
        {
            lock (_lockObject)
            {
                var fresh = Guid.NewGuid().ToString();
                WriteAtomic(fresh, logger);
                SaveSessionCreatedAtInternal(DateTime.UtcNow);
                // Earlier failed runs belonged to the refused session id.
                TryDelete(_registrationFailurePath, logger);
                logger?.Info($"SessionIdPersistence: rotated SessionId to {fresh}.");
                return fresh;
            }
        }

        private RegistrationFailureRecord LoadRegistrationFailureInternal()
        {
            try
            {
                if (!File.Exists(_registrationFailurePath)) return null;
                var json = JObject.Parse(File.ReadAllText(_registrationFailurePath));
                var summary = json.ToObject<RegistrationFailureSummary>();
                return summary != null && summary.FailedRuns > 0
                    ? new RegistrationFailureRecord(
                        summary,
                        (string)json[LocalNetworkFingerprintKey],
                        json[LocalDeviceBootKey]?.Type == JTokenType.Date ? json[LocalDeviceBootKey].ToObject<DateTime>().ToUniversalTime() : (DateTime?)null,
                        (string)json[LocalCertThumbprintKey])
                    : null;
            }
            catch
            {
                // Corrupt record: report nothing rather than a half-read account.
                return null;
            }
        }

        /// <summary>
        /// The summary's own properties plus the agent-local keys: they sit next to the wire type,
        /// never inside it (the summary deserializes without them).
        /// </summary>
        private void WriteRegistrationFailure(
            RegistrationFailureSummary summary, string networkFingerprint, DateTime? deviceBootUtc, string certThumbprint)
        {
            var json = JObject.FromObject(summary);
            if (!string.IsNullOrEmpty(networkFingerprint))
                json[LocalNetworkFingerprintKey] = networkFingerprint;
            if (deviceBootUtc.HasValue)
                json[LocalDeviceBootKey] = deviceBootUtc.Value.ToUniversalTime();
            if (!string.IsNullOrEmpty(certThumbprint))
                json[LocalCertThumbprintKey] = certThumbprint;

            var bytes = Encoding.UTF8.GetBytes(json.ToString(Formatting.None));
            using (var stream = new FileStream(
                _registrationFailurePath, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 4096, options: FileOptions.WriteThrough))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }
        }

        private static void TryDelete(string path, AgentLogger logger)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) { logger?.Warning($"SessionIdPersistence: failed to delete {Path.GetFileName(path)}: {ex.Message}"); }
        }

        private void SaveSessionCreatedAtInternal(DateTime createdAtUtc)
        {
            if (createdAtUtc.Kind != DateTimeKind.Utc) createdAtUtc = createdAtUtc.ToUniversalTime();
            try { File.WriteAllText(_sessionCreatedFilePath, createdAtUtc.ToString("O")); }
            catch { /* best-effort — watchdog will fall back to initialising it on next start */ }
        }

        private void WriteAtomic(string sessionId, AgentLogger logger)
        {
            var tempPath = _sessionFilePath + ".tmp";
            try
            {
                File.WriteAllText(tempPath, sessionId);

                if (File.Exists(_sessionFilePath))
                {
                    // File.Replace preserves ACLs + is atomic on NTFS.
                    File.Replace(tempPath, _sessionFilePath, destinationBackupFileName: null);
                }
                else
                {
                    File.Move(tempPath, _sessionFilePath);
                }
            }
            catch (Exception ex)
            {
                logger?.Error($"SessionIdPersistence: atomic write failed ({ex.Message}) — falling back to direct write.", ex);
                File.WriteAllText(_sessionFilePath, sessionId);
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            }
        }
    }

    /// <summary>
    /// <c>registration-failure.json</c> as the agent keeps it: the <see cref="RegistrationFailureSummary"/>
    /// the emergency break may send, plus the network fingerprint, boot time and client-certificate
    /// thumbprint of the latest failed run, which stay on the device — only the next successful
    /// start's comparisons (changed or not) are reported.
    /// </summary>
    public sealed class RegistrationFailureRecord
    {
        public RegistrationFailureRecord(
            RegistrationFailureSummary summary, string networkFingerprint,
            DateTime? deviceBootUtc = null, string certThumbprint = null)
        {
            Summary = summary ?? throw new ArgumentNullException(nameof(summary));
            NetworkFingerprint = networkFingerprint;
            DeviceBootUtc = deviceBootUtc;
            CertThumbprint = certThumbprint;
        }

        public RegistrationFailureSummary Summary { get; }

        /// <summary>Fingerprint of the latest failed run's network; null when it was unknown.</summary>
        public string NetworkFingerprint { get; }

        /// <summary>Boot time (UTC) of the latest failed run; null when it was unknown or the record predates it.</summary>
        public DateTime? DeviceBootUtc { get; }

        /// <summary>Thumbprint of the client certificate the latest failed run used; null when none or unknown.</summary>
        public string CertThumbprint { get; }
    }
}
