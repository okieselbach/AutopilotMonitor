using System;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Shared.DataAccess
{
    /// <summary>
    /// The block record a session carries once the platform blocked its device — written when the
    /// block is placed and surfaced as the <c>Block*</c> fields of <see cref="SessionSummary"/>.
    /// </summary>
    public sealed class SessionBlockRecord
    {
        public DateTime BlockedAt { get; set; }

        /// <summary><see cref="DeviceBlockActions"/> value.</summary>
        public string Action { get; set; } = DeviceBlockActions.Block;

        public DateTime? BlockedUntil { get; set; }

        /// <summary><see cref="SessionBlockCauses"/> value.</summary>
        public string Cause { get; set; } = SessionBlockCauses.EventLimit;

        /// <summary>Only for <see cref="SessionBlockCauses.EventLimit"/>.</summary>
        public int? EventCount { get; set; }

        /// <summary>Only for <see cref="SessionBlockCauses.EventLimit"/>.</summary>
        public int? EventThreshold { get; set; }
    }
}
