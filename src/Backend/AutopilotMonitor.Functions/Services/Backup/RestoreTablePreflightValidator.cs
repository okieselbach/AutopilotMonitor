using System;
using System.Linq;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models.Backup;

namespace AutopilotMonitor.Functions.Services.Backup
{
    /// <summary>
    /// Light, no-I/O pre-flight for the restore HTTP triggers (plan §Wave17 #2 +
    /// Wave18 #2). Validates only the request shape — table name is in the
    /// critical-backup catalog, mode is a known value, keys are present, auth-table
    /// rules are respected. Heavier work (manifest parse, SHA verify, blob ETag-pin)
    /// lives in <see cref="BackupRestoreInputValidator"/>.
    /// <para>
    /// Single-row preview is read-only against the live row and does not require
    /// the destructive-acknowledgement / confirmation-token rituals the full-table
    /// restore demands. The auth-table single-row path is explicitly allowed (with
    /// a UI banner), full-table on auth-tables stays blocked at this gate.
    /// </para>
    /// </summary>
    public sealed class RestoreTablePreflightValidator
    {
        /// <summary>
        /// Validates a single-row restore request. Throws on the first failure with
        /// a code suitable for HTTP envelope. Pure function, fully unit-testable.
        /// </summary>
        public void ValidateRowRequest(string backupId, RestoreRowRequest request)
        {
            if (string.IsNullOrEmpty(backupId))
            {
                throw new BackupTerminalException(Constants.BackupErrorCodes.InvalidBackupId, "backupId route segment is empty");
            }
            if (request == null)
            {
                throw new BackupTerminalException(Constants.BackupErrorCodes.MissingBody, "request body is required");
            }

            if (string.IsNullOrEmpty(request.TableName))
            {
                throw new BackupTerminalException(Constants.BackupErrorCodes.InvalidTable, "tableName is required");
            }
            if (!Constants.CriticalBackupTables.All.Contains(request.TableName, StringComparer.Ordinal))
            {
                throw new BackupTerminalException(
                    Constants.BackupErrorCodes.InvalidTable,
                    $"tableName '{request.TableName}' is not in the critical-backup catalog — only the 15 critical tables are restorable");
            }

            // Empty PK/RK are technically legal in Azure Tables, so we only insist on
            // non-null. The Validator further down may still reject if the dump line
            // is not found for the given keys.
            if (request.PartitionKey == null)
            {
                throw new BackupTerminalException(Constants.BackupErrorCodes.InvalidKeys, "partitionKey is required (may be empty string but not null)");
            }
            if (request.RowKey == null)
            {
                throw new BackupTerminalException(Constants.BackupErrorCodes.InvalidKeys, "rowKey is required (may be empty string but not null)");
            }

            if (request.Mode != RestoreRowMode.Preview && request.Mode != RestoreRowMode.Commit)
            {
                throw new BackupTerminalException(Constants.BackupErrorCodes.InvalidMode, $"mode must be 'preview' or 'commit', got '{request.Mode}'");
            }

            if (request.Mode == RestoreRowMode.Commit)
            {
                if (string.IsNullOrEmpty(request.IfSha256))
                {
                    throw new BackupTerminalException(
                        Constants.BackupErrorCodes.MissingPrecondition,
                        "ifSha256 is required on commit — echo the rowSha256 from the preview response");
                }
                if (!IsLowerHex64(request.IfSha256))
                {
                    throw new BackupTerminalException(
                        Constants.BackupErrorCodes.MissingPrecondition,
                        "ifSha256 must be 64 lowercase hex characters (SHA-256 hex digest)");
                }
                // ifCurrentETag is intentionally nullable: null carries the precondition
                // "preview saw no live row" and triggers AddEntity on commit.
            }
        }

        /// <summary>
        /// True for tables whose IsEnabled column carries security semantics — UI
        /// must render a warning banner and the operator must confirm explicitly.
        /// Single-row commit on these is allowed (full-table is not).
        /// </summary>
        public bool IsAuthTable(string tableName)
        {
            return Constants.CriticalBackupTables.AuthTablesFullRestoreForbidden
                .Contains(tableName, StringComparer.Ordinal);
        }

        private static bool IsLowerHex64(string s)
        {
            if (s.Length != 64) return false;
            for (var i = 0; i < s.Length; i++)
            {
                var c = s[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            }
            return true;
        }
    }
}
