using System;
using System.IO;
using AutopilotMonitor.Agent.V2.Core.Logging;

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
        private readonly object _lockObject = new object();

        public SessionIdPersistence(string dataDirectory)
        {
            if (string.IsNullOrWhiteSpace(dataDirectory))
                throw new ArgumentNullException(nameof(dataDirectory));

            if (!Directory.Exists(dataDirectory))
                Directory.CreateDirectory(dataDirectory);

            _sessionFilePath = Path.Combine(dataDirectory, "session.id");
            _sessionCreatedFilePath = Path.Combine(dataDirectory, "session.created");
            _whiteGloveMarkerPath = Path.Combine(dataDirectory, "whiteglove.complete");
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

        /// <summary>Deletes the persisted SessionId. Next <see cref="GetOrCreate"/> starts a fresh session.</summary>
        public void Delete(AgentLogger logger = null)
        {
            lock (_lockObject)
            {
                TryDelete(_sessionFilePath, logger);
                TryDelete(_sessionCreatedFilePath, logger);
                TryDelete(_whiteGloveMarkerPath, logger);
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
                logger?.Info($"SessionIdPersistence: rotated SessionId to {fresh}.");
                return fresh;
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
}
