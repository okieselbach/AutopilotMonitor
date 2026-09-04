#nullable enable
using System;
using System.Collections.Generic;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime
{
    /// <summary>
    /// A writer era: the stretch of log written by ONE process lifetime, i.e. under one
    /// timezone belief. For the IME service family (IntuneManagementExtension*.log) an era
    /// starts at an "EMS Agent Started" entry and may continue across a log rotation into the
    /// next file; for AgentExecutor.log every script execution is its own short-lived process
    /// and therefore its own era. An era carries an offset only when an anchor inside it was
    /// matched against an instant the agent knows in UTC (<see cref="ImeLogEraPreScan"/>).
    /// </summary>
    internal sealed class CmTraceEra
    {
        /// <summary>File segments (file name → [start, end) byte range) that belong to this era, in write order.</summary>
        public List<CmTraceEraSegment> Segments { get; } = new List<CmTraceEraSegment>();

        /// <summary>Local time of the era's first entry as written (null for a leading era whose start predates the scanned region).</summary>
        public DateTime? StartLocal { get; set; }

        /// <summary>Local time of the next era's first entry (null for the last era).</summary>
        public DateTime? EndLocal { get; set; }

        /// <summary>The writer's UTC offset (local = UTC + offset) when anchored.</summary>
        public TimeSpan? Offset { get; set; }

        /// <summary>What established <see cref="Offset"/> ("bootstrap-policy-result", "bootstrap-stdout", …).</summary>
        public string? AnchorKind { get; set; }

        public bool IsAnchored => Offset.HasValue;
    }

    internal sealed class CmTraceEraSegment
    {
        public CmTraceEraSegment(string fileName, long startOffset, long endOffset)
        {
            FileName = fileName;
            StartOffset = startOffset;
            EndOffset = endOffset;
        }

        public string FileName { get; }
        public long StartOffset { get; }
        /// <summary><c>long.MaxValue</c> while the era is still open in this file.</summary>
        public long EndOffset { get; set; }

        public bool Contains(string fileName, long offset) =>
            string.Equals(FileName, fileName, StringComparison.OrdinalIgnoreCase)
            && offset >= StartOffset && offset < EndOffset;
    }

    /// <summary>
    /// Era lookup for backlog lines (2026-09-04, session a7140f98). Two lookups:
    /// <list type="bullet">
    /// <item><description><see cref="TryResolveByOffset"/> — the line's own file and byte offset
    ///   (IME service family and AgentExecutor.log: eras are byte ranges).</description></item>
    /// <item><description><see cref="TryResolveByLocalTime"/> — transfer for files written by the
    ///   SAME process as the IME service log but without their own era marker (AppWorkload.log,
    ///   AppActionProcessor.log: plugins hosted in IntuneManagementExtension.exe, verified in the
    ///   decompiled build). Within one process every file shares the zone belief, so a local
    ///   timestamp places the line in the service era whose local range contains it. Disabled
    ///   entirely when the service eras' local ranges are not monotonic (a westward zone change
    ///   between two service starts makes the ranges overlap) — ambiguity must never anchor.</description></item>
    /// </list>
    /// Everything not resolvable here stays on the marked reader-zone fallback; a partially
    /// corrected era is never produced because an anchor applies to a whole era or not at all.
    /// </summary>
    internal sealed class CmTraceEraTable
    {
        private readonly List<CmTraceEra> _offsetEras = new List<CmTraceEra>();
        private readonly List<CmTraceEra> _serviceEras = new List<CmTraceEra>();
        private bool _transferAmbiguous;

        public IReadOnlyList<CmTraceEra> ServiceEras => _serviceEras;
        public bool TransferAmbiguous => _transferAmbiguous;
        public int AnchoredEraCount
        {
            get
            {
                var n = 0;
                foreach (var e in _offsetEras) if (e.IsAnchored) n++;
                return n;
            }
        }

        /// <summary>Register an era addressable by (file, offset). Service eras also take part in the local-time transfer.</summary>
        public void Add(CmTraceEra era, bool isServiceEra)
        {
            _offsetEras.Add(era);
            if (isServiceEra) _serviceEras.Add(era);
        }

        /// <summary>Call once after all service eras are added: validates the local ranges used by the transfer.</summary>
        public void Seal()
        {
            _transferAmbiguous = false;
            DateTime? previousStart = null;
            foreach (var era in _serviceEras)
            {
                if (!era.StartLocal.HasValue) continue;
                if (previousStart.HasValue && era.StartLocal.Value <= previousStart.Value)
                {
                    _transferAmbiguous = true;
                    break;
                }
                previousStart = era.StartLocal;
            }
        }

        public bool TryResolveByOffset(string fileName, long offset, out TimeSpan writerOffset, out string anchorKind)
        {
            writerOffset = TimeSpan.Zero;
            anchorKind = string.Empty;
            if (string.IsNullOrEmpty(fileName) || offset < 0) return false;

            foreach (var era in _offsetEras)
            {
                if (!era.IsAnchored) continue;
                foreach (var seg in era.Segments)
                {
                    if (seg.Contains(fileName, offset))
                    {
                        writerOffset = era.Offset!.Value;
                        anchorKind = era.AnchorKind ?? string.Empty;
                        return true;
                    }
                }
            }
            return false;
        }

        public bool TryResolveByLocalTime(DateTime localTimestamp, out TimeSpan writerOffset, out string anchorKind)
        {
            writerOffset = TimeSpan.Zero;
            anchorKind = string.Empty;
            if (_transferAmbiguous) return false;

            var local = DateTime.SpecifyKind(localTimestamp, DateTimeKind.Unspecified);
            foreach (var era in _serviceEras)
            {
                if (!era.IsAnchored) continue;
                // A leading era with unknown start covers everything before its end.
                var afterStart = !era.StartLocal.HasValue || local >= era.StartLocal.Value;
                var beforeEnd = !era.EndLocal.HasValue || local < era.EndLocal.Value;
                if (afterStart && beforeEnd)
                {
                    writerOffset = era.Offset!.Value;
                    anchorKind = era.AnchorKind + "/service-era-transfer";
                    return true;
                }
            }
            return false;
        }
    }
}
