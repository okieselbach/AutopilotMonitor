#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Shared.Logging;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime
{
    /// <summary>
    /// Builds the <see cref="CmTraceEraTable"/> for the backlog the tracker is about to read
    /// (2026-09-04, session a7140f98: the IME service wrote the whole device-ESP era under the
    /// OOBE default zone, Windows moved the zone before the agent started, and the restart
    /// replay put every pre-reboot line three hours early — a 20-minute DeviceSetup became a
    /// "blocking app timeout").
    /// <para>
    /// Backlog lines can never self-anchor (their age would round onto the offset grid), so an
    /// era needs an entry whose UTC instant is known independently. The one such entry every
    /// platform-script bootstrap leaves behind is the bootstrap script's own execution: the
    /// install marker (<c>HKLM\SOFTWARE\AutopilotMonitor\Deployed</c>, UTC) is written
    /// milliseconds before the script exits, and the IME logs that exit twice — AgentExecutor.log
    /// records the script's stdout ("Bootstrap Completed Successfully"), and the service log
    /// records "[PowerShell] … Policy id = &lt;bootstrap&gt;, policy result = …". Field measurement
    /// (4 sessions, 2026-09-04): 0.13–0.28 s between marker and stdout record, 0.02–0.08 s more to
    /// the service record — against a 15-minute grid with a 2-minute residual guard.
    /// </para>
    /// <para>
    /// Rules that keep this sound: only the FIRST bootstrap record of each file is offered (a
    /// SKIP re-run minutes later could round onto the grid); the residual guard is
    /// <see cref="CmTraceOffsetCalibrator.TryMeasureOffset"/>; an anchor applies to the whole
    /// era between two "EMS Agent Started" entries (service) or one script execution
    /// (AgentExecutor) and never beyond; nothing is anchored without a marker (MSI bootstrap,
    /// replay tooling). Everything else stays on the marked reader-zone fallback.
    /// </para>
    /// </summary>
    internal static class ImeLogEraPreScan
    {
        internal const string AnchorKindBootstrapStdout = "bootstrap-stdout";
        internal const string AnchorKindBootstrapPolicyResult = "bootstrap-policy-result";

        private const string ServiceStartedPrefix = "EMS Agent Started";
        private const string ExecutorScriptStartPrefix = "Adding argument powershell with value ";
        private const string ExecutorOutputPrefix = "write output done.";
        private const string BootstrapInstallRunMarker = "Bootstrap Completed Successfully";
        private const string BootstrapInstallRunMarkerAlt = "Agent install mode completed successfully";

        private static readonly Regex ExecutorScriptPolicyId = new Regex(
            @"Policies\\Scripts\\[a-z0-9\-]+_(?<id>[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12})\.ps1",
            RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

        private static readonly Regex ServicePolicyResult = new Regex(
            @"^\[PowerShell\] User Id = [a-f0-9\-]+, Policy id = (?<id>[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}), policy result = ",
            RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

        private const int MaxEntryLines = 100;

        public sealed class ScanInput
        {
            public ScanInput(string filePath, long startOffset)
            {
                FilePath = filePath;
                StartOffset = startOffset;
            }

            public string FilePath { get; }
            public long StartOffset { get; }
        }

        public static bool IsServiceLogFile(string fileName) =>
            fileName.StartsWith("IntuneManagementExtension", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("_IntuneManagementExtension", StringComparison.OrdinalIgnoreCase);

        public static bool IsExecutorLogFile(string fileName) =>
            fileName.StartsWith("AgentExecutor", StringComparison.OrdinalIgnoreCase);

        /// <summary>Files written in-process by the IME service without an era marker of their own (transfer by local time).</summary>
        public static bool IsServiceHostedFile(string fileName) =>
            fileName.StartsWith("AppWorkload", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("AppActionProcessor", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Scan the given files (sort order = write order; archived before current) from their
        /// start offsets to EOF and return the era table. Never throws; a file that cannot be
        /// read contributes no eras.
        /// </summary>
        public static CmTraceEraTable Build(
            IReadOnlyList<ScanInput> files,
            DateTime? deployedUtc,
            int maxEntryBytes,
            AgentLogger? logger)
        {
            var table = new CmTraceEraTable();
            if (!deployedUtc.HasValue)
            {
                logger?.Info("ImeLogEraPreScan: no Deployed marker — backlog eras stay on the reader-zone fallback");
                return table;
            }

            string? bootstrapPolicyId = null;

            // AgentExecutor first: it is where the bootstrap policy id becomes known.
            foreach (var input in files)
            {
                if (!IsExecutorLogFile(Path.GetFileName(input.FilePath))) continue;
                try { ScanExecutorFile(input, deployedUtc.Value, maxEntryBytes, table, ref bootstrapPolicyId, logger); }
                catch (Exception ex) { logger?.Debug($"ImeLogEraPreScan: {Path.GetFileName(input.FilePath)} skipped: {ex.Message}"); }
            }

            var serviceFiles = new List<ScanInput>();
            foreach (var input in files)
                if (IsServiceLogFile(Path.GetFileName(input.FilePath))) serviceFiles.Add(input);

            try { ScanServiceFiles(serviceFiles, deployedUtc.Value, maxEntryBytes, table, bootstrapPolicyId, logger); }
            catch (Exception ex) { logger?.Debug($"ImeLogEraPreScan: service scan aborted: {ex.Message}"); }

            table.Seal();

            logger?.Info(
                $"ImeLogEraPreScan: {table.AnchoredEraCount} anchored era(s), {table.ServiceEras.Count} service era(s), " +
                $"bootstrapPolicyId={(bootstrapPolicyId ?? "(unknown)")}, transfer={(table.TransferAmbiguous ? "ambiguous-disabled" : "enabled")}");
            return table;
        }

        private static void ScanExecutorFile(
            ScanInput input, DateTime deployedUtc, int maxEntryBytes, CmTraceEraTable table,
            ref string? bootstrapPolicyId, AgentLogger? logger)
        {
            var fileName = Path.GetFileName(input.FilePath);
            CmTraceEra? current = null;
            string? currentPolicyId = null;
            var anchored = false;

            foreach (var e in ReadEntries(input, maxEntryBytes))
            {
                if (e.Message.StartsWith(ExecutorScriptStartPrefix, StringComparison.Ordinal))
                {
                    if (current != null) current.Segments[0].EndOffset = e.Offset;
                    current = new CmTraceEra();
                    current.Segments.Add(new CmTraceEraSegment(fileName, e.Offset, long.MaxValue));
                    current.StartLocal = e.HasTimestamp ? e.LocalTimestamp : (DateTime?)null;
                    table.Add(current, isServiceEra: false);
                    var m = ExecutorScriptPolicyId.Match(e.Message);
                    currentPolicyId = m.Success ? m.Groups["id"].Value : null;
                    continue;
                }

                if (anchored || current == null || !e.HasTimestamp) continue;
                if (!e.Message.StartsWith(ExecutorOutputPrefix, StringComparison.Ordinal)) continue;
                if (e.Message.IndexOf(BootstrapInstallRunMarker, StringComparison.OrdinalIgnoreCase) < 0
                    && e.Message.IndexOf(BootstrapInstallRunMarkerAlt, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                // First bootstrap install record of this file, offered once — accepted or not.
                anchored = true;
                if (CmTraceOffsetCalibrator.TryMeasureOffset(e.LocalTimestamp, deployedUtc, out var offset))
                {
                    current.Offset = offset;
                    current.AnchorKind = AnchorKindBootstrapStdout;
                    if (bootstrapPolicyId == null) bootstrapPolicyId = currentPolicyId;
                    logger?.Info($"ImeLogEraPreScan: {fileName} bootstrap execution anchored at {offset} (stdout local={e.LocalTimestamp:yyyy-MM-ddTHH:mm:ss.fff}, policy={currentPolicyId ?? "?"})");
                }
                else
                {
                    logger?.Info($"ImeLogEraPreScan: {fileName} bootstrap stdout record rejected by the residual guard (local={e.LocalTimestamp:yyyy-MM-ddTHH:mm:ss.fff}, deployed={deployedUtc:HH:mm:ss}Z)");
                    if (bootstrapPolicyId == null) bootstrapPolicyId = currentPolicyId;
                }
            }
        }

        private static void ScanServiceFiles(
            IReadOnlyList<ScanInput> files, DateTime deployedUtc, int maxEntryBytes, CmTraceEraTable table,
            string? bootstrapPolicyId, AgentLogger? logger)
        {
            if (files.Count == 0) return;

            // The leading era: whatever process was writing when the scanned region begins.
            var current = new CmTraceEra();
            table.Add(current, isServiceEra: true);
            var resultOffered = false;

            foreach (var input in files)
            {
                var fileName = Path.GetFileName(input.FilePath);
                var segment = new CmTraceEraSegment(fileName, input.StartOffset, long.MaxValue);
                current.Segments.Add(segment);

                foreach (var e in ReadEntries(input, maxEntryBytes))
                {
                    if (e.Message.StartsWith(ServiceStartedPrefix, StringComparison.Ordinal))
                    {
                        segment.EndOffset = e.Offset;
                        var next = new CmTraceEra { StartLocal = e.HasTimestamp ? e.LocalTimestamp : (DateTime?)null };
                        current.EndLocal = next.StartLocal;
                        segment = new CmTraceEraSegment(fileName, e.Offset, long.MaxValue);
                        next.Segments.Add(segment);
                        table.Add(next, isServiceEra: true);
                        current = next;
                        continue;
                    }

                    if (resultOffered || bootstrapPolicyId == null || !e.HasTimestamp) continue;
                    if (!e.Message.StartsWith("[PowerShell] User Id = ", StringComparison.Ordinal)) continue;
                    var m = ServicePolicyResult.Match(e.Message);
                    if (!m.Success || !string.Equals(m.Groups["id"].Value, bootstrapPolicyId, StringComparison.OrdinalIgnoreCase)) continue;

                    // First result record for the bootstrap policy, offered once.
                    resultOffered = true;
                    if (CmTraceOffsetCalibrator.TryMeasureOffset(e.LocalTimestamp, deployedUtc, out var offset))
                    {
                        current.Offset = offset;
                        current.AnchorKind = AnchorKindBootstrapPolicyResult;
                        logger?.Info($"ImeLogEraPreScan: {fileName} service era anchored at {offset} (policy result local={e.LocalTimestamp:yyyy-MM-ddTHH:mm:ss.fff})");
                    }
                    else
                    {
                        logger?.Info($"ImeLogEraPreScan: {fileName} bootstrap policy result rejected by the residual guard (local={e.LocalTimestamp:yyyy-MM-ddTHH:mm:ss.fff}, deployed={deployedUtc:HH:mm:ss}Z)");
                    }
                }

                // The era continues into the next (newer) file: close this file's segment at EOF.
                try { segment.EndOffset = new FileInfo(input.FilePath).Length; } catch { segment.EndOffset = long.MaxValue; }
            }

            // The last era stays open in its last file.
            var last = current.Segments[current.Segments.Count - 1];
            last.EndOffset = long.MaxValue;
        }

        private sealed class ScannedEntry
        {
            public long Offset;
            public bool HasTimestamp;
            public DateTime LocalTimestamp;
            public string Message = string.Empty;
        }

        /// <summary>
        /// Enumerate CMTrace entries with their file offsets. Same multiline assembly as the
        /// tracker's read loop (an AgentExecutor stdout record spans many lines; the closing tag
        /// carries the time); oversized or over-long entries are dropped, never partially used.
        /// </summary>
        private static IEnumerable<ScannedEntry> ReadEntries(ScanInput input, int maxEntryBytes)
        {
            using (var stream = new FileStream(input.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (input.StartOffset > 0 && input.StartOffset < stream.Length)
                    stream.Seek(input.StartOffset, SeekOrigin.Begin);
                else if (input.StartOffset >= stream.Length)
                    yield break;

                var reader = new BoundedLineReader(stream, maxEntryBytes);
                StringBuilder? buffer = null;
                var bufferLines = 0;
                long entryStart = input.StartOffset;
                var skipping = false;

                while (true)
                {
                    var line = reader.ReadLineAsync(System.Threading.CancellationToken.None).GetAwaiter().GetResult();
                    if (line == null) yield break;
                    if (buffer == null) entryStart = reader.LastLineStart;

                    if (reader.LastLineTruncated)
                    {
                        buffer = null; bufferLines = 0; skipping = true;
                        continue;
                    }

                    if (skipping)
                    {
                        if (line.StartsWith("<![LOG[", StringComparison.Ordinal)) skipping = false;
                        else
                        {
                            if (line.Contains("]LOG]!>")) skipping = false;
                            continue;
                        }
                    }

                    if (buffer != null)
                    {
                        buffer.Append('\n').Append(line);
                        bufferLines++;
                        if (line.Contains("]LOG]!>"))
                        {
                            line = buffer.ToString();
                            buffer = null; bufferLines = 0;
                        }
                        else if (bufferLines >= MaxEntryLines || buffer.Length >= maxEntryBytes)
                        {
                            buffer = null; bufferLines = 0; skipping = true;
                            continue;
                        }
                        else continue;
                    }
                    else if (line.StartsWith("<![LOG[", StringComparison.Ordinal) && !line.Contains("]LOG]!>"))
                    {
                        buffer = new StringBuilder(line);
                        bufferLines = 1;
                        continue;
                    }

                    if (!CmTraceLogParser.TryParseLine(line, out var entry) || entry == null) continue;
                    yield return new ScannedEntry
                    {
                        Offset = entryStart,
                        HasTimestamp = entry.HasTimestamp && !entry.BiasMinutes.HasValue,
                        LocalTimestamp = entry.LocalTimestamp,
                        Message = entry.Message ?? string.Empty,
                    };
                }
            }
        }
    }
}
