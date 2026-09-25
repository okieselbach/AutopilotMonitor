using System;
using System.Collections.Generic;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Shared.DataAccess
{
    /// <summary>
    /// Outcome of <see cref="IConfigRepository.UpdateAdminConfigurationAsync"/>: either the mutation
    /// rejected the change (<see cref="Error"/> set, nothing written) or it was accepted, in which case
    /// <see cref="ChangedColumns"/> names the columns actually written (empty = no-op, no write).
    /// </summary>
    public sealed class AdminConfigurationUpdateResult
    {
        /// <summary>The mutation's rejection message; null when the change was accepted.</summary>
        public string? Error { get; set; }

        /// <summary>The stored configuration the mutation started from (fresh read).</summary>
        public AdminConfiguration Before { get; set; } = default!;

        /// <summary>The configuration after the mutation — what the row holds now when accepted.</summary>
        public AdminConfiguration After { get; set; } = default!;

        /// <summary>Columns written, server stamps excluded. Empty when the mutation changed nothing.</summary>
        public IReadOnlyCollection<string> ChangedColumns { get; set; } = Array.Empty<string>();
    }
}
