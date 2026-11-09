using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>What an allowed-list entry lets a local account be.</summary>
    public enum LocalAccountRole
    {
        /// <summary>Any role, local administrator included — an entry without qualifier.</summary>
        Any = 0,

        /// <summary>Only while the account is not a direct member of the local Administrators group (<c>name|user</c>).</summary>
        StandardUser = 1,
    }

    /// <summary>What a built-in allowed-list entry is matched against.</summary>
    public enum LocalAdminBuiltInMatch
    {
        /// <summary>The well-known RID of a built-in account on the machine SID — the same in every language.</summary>
        Rid = 0,

        /// <summary>An account name; the C:\Users folder of the same name is allowed with it.</summary>
        AccountName = 1,

        /// <summary>A C:\Users folder that is never a local account.</summary>
        ProfileFolder = 2,
    }

    /// <summary>One built-in entry of the Local Admin Analyzer's allowed list.</summary>
    public sealed class LocalAdminBuiltInEntry
    {
        public LocalAdminBuiltInEntry(string name, LocalAdminBuiltInMatch match, LocalAccountRole role, int? rid = null)
        {
            Name = name;
            Match = match;
            Role = role;
            Rid = rid;
        }

        /// <summary>Account or folder name; for a RID entry the English name of the account.</summary>
        public string Name { get; }

        public LocalAdminBuiltInMatch Match { get; }

        public LocalAccountRole Role { get; }

        /// <summary>The well-known RID of a RID entry, otherwise null.</summary>
        public int? Rid { get; }
    }

    /// <summary>
    /// The Local Admin Analyzer's allowed list: the built-in entries and the grammar of the tenant's
    /// entries (<see cref="TenantConfiguration.LocalAdminAllowedAccountsJson"/>). Single source for the
    /// agent's evaluation, the backend's write validation and the portal (shared manifest).
    /// <para>
    /// A tenant entry is an account name or a glob (<c>*</c>, <c>?</c>), optionally followed by
    /// <c>|user</c>: the account is then allowed only while it is not a member of the local
    /// Administrators group. Windows account names cannot contain '|', so the separator is unambiguous,
    /// and an agent that predates it matches such an entry against no account (fail-safe).
    /// </para>
    /// </summary>
    public static class LocalAdminAllowList
    {
        public const char RoleSeparator = '|';

        public const string StandardUserQualifier = "user";

        public const int MaxEntries = 200;

        public const int MaxEntryLength = 64;

        /// <summary>A glob needs this many characters besides <c>*</c> and <c>?</c> — a bare <c>*</c> would switch the analyzer off.</summary>
        public const int MinPatternLiterals = 2;

        /// <summary>Characters Windows rejects in account names, apart from the separator and the glob characters.</summary>
        public const string ForbiddenNameCharacters = "\"/\\[]:;=,+<>";

        /// <summary>
        /// The built-in entries. The four built-in accounts are decided by their RID alone: no name
        /// entry, built-in or tenant, can lift "Guest is never an administrator" or flag the real
        /// Administrator. The name entries cover OTHER accounts carrying a built-in name — a decoy without
        /// admin rights is fine, an administrator is not.
        /// </summary>
        public static IReadOnlyList<LocalAdminBuiltInEntry> BuiltIns { get; } = new LocalAdminBuiltInEntry[]
        {
            new("Administrator", LocalAdminBuiltInMatch.Rid, LocalAccountRole.Any, 500),
            new("Guest", LocalAdminBuiltInMatch.Rid, LocalAccountRole.StandardUser, 501),
            new("DefaultAccount", LocalAdminBuiltInMatch.Rid, LocalAccountRole.StandardUser, 503),
            new("WDAGUtilityAccount", LocalAdminBuiltInMatch.Rid, LocalAccountRole.StandardUser, 504),

            new("Administrator", LocalAdminBuiltInMatch.AccountName, LocalAccountRole.StandardUser),
            new("Guest", LocalAdminBuiltInMatch.AccountName, LocalAccountRole.StandardUser),
            new("DefaultAccount", LocalAdminBuiltInMatch.AccountName, LocalAccountRole.StandardUser),
            new("WDAGUtilityAccount", LocalAdminBuiltInMatch.AccountName, LocalAccountRole.StandardUser),

            // The OOBE account is an Administrators member during the ESP.
            new("defaultuser0", LocalAdminBuiltInMatch.AccountName, LocalAccountRole.Any),
            new("defaultuser1", LocalAdminBuiltInMatch.AccountName, LocalAccountRole.Any),
            new("defaultuser2", LocalAdminBuiltInMatch.AccountName, LocalAccountRole.Any),

            // Assigned Access autologon account: a local standard user by design.
            new("kioskUser0", LocalAdminBuiltInMatch.AccountName, LocalAccountRole.StandardUser),

            // Web sign-in's system-managed account, enabled for each web sign-in.
            new("WsiAccount", LocalAdminBuiltInMatch.AccountName, LocalAccountRole.StandardUser),

            new("Public", LocalAdminBuiltInMatch.ProfileFolder, LocalAccountRole.Any),
            new("Default", LocalAdminBuiltInMatch.ProfileFolder, LocalAccountRole.Any),
            new("Default User", LocalAdminBuiltInMatch.ProfileFolder, LocalAccountRole.Any),
            new("All Users", LocalAdminBuiltInMatch.ProfileFolder, LocalAccountRole.Any),
        };

        /// <summary>
        /// Parses a tenant entry. An entry without <see cref="RoleSeparator"/> is the pattern verbatim
        /// (role Any) — exactly what it meant before qualifiers existed. With the separator, only
        /// <c>pattern|user</c> parses (qualifier case-insensitive, no surrounding spaces); anything else
        /// returns false and must allow nothing.
        /// </summary>
        public static bool TryParseEntry(string? raw, [NotNullWhen(true)] out string? pattern, out LocalAccountRole role)
        {
            pattern = null;
            role = LocalAccountRole.Any;
            if (raw is null || string.IsNullOrWhiteSpace(raw))
                return false;

            var separator = raw.IndexOf(RoleSeparator);
            if (separator < 0)
            {
                pattern = raw;
                return true;
            }

            var name = raw.Substring(0, separator);
            var qualifier = raw.Substring(separator + 1);
            if (name.Length == 0 || name.Trim().Length != name.Length
                || !string.Equals(qualifier, StandardUserQualifier, StringComparison.OrdinalIgnoreCase))
                return false;

            pattern = name;
            role = LocalAccountRole.StandardUser;
            return true;
        }

        /// <summary>The canonical entry text: the pattern, plus <c>|user</c> for a standard-user entry.</summary>
        public static string Format(string pattern, LocalAccountRole role)
            => role == LocalAccountRole.StandardUser ? pattern + RoleSeparator + StandardUserQualifier : pattern;

        /// <summary>True for a glob with fewer than <see cref="MinPatternLiterals"/> characters besides <c>*</c> and <c>?</c>.</summary>
        public static bool IsTooBroad(string pattern)
        {
            if (pattern.IndexOf('*') < 0 && pattern.IndexOf('?') < 0)
                return false;

            var literals = 0;
            foreach (var ch in pattern)
            {
                if (ch != '*' && ch != '?')
                    literals++;
            }
            return literals < MinPatternLiterals;
        }

        /// <summary>
        /// Checks an entry a caller is about to ADD. Returns a user-facing reason, or null when the entry
        /// may be stored. Stored entries are never re-checked: they keep their meaning even where they
        /// would not pass today.
        /// </summary>
        public static string? ValidateNewEntry(string? raw)
        {
            if (raw is null || string.IsNullOrWhiteSpace(raw))
                return "entries must not be empty.";
            if (raw.Length > MaxEntryLength)
                return $"each entry must be at most {MaxEntryLength} characters.";
            foreach (var ch in raw)
            {
                if (char.IsControl(ch))
                    return "entries must not contain control characters.";
            }
            if (raw.Trim().Length != raw.Length)
                return $"\"{raw}\" must not start or end with a space.";
            if (!TryParseEntry(raw, out var pattern, out _))
                return $"\"{raw}\" is not valid: use an account name or pattern, optionally followed by \"{RoleSeparator}{StandardUserQualifier}\" (standard user only).";
            if (pattern.IndexOfAny(ForbiddenNameCharacters.ToCharArray()) >= 0)
                return $"\"{raw}\" contains a character account names cannot have ({string.Join(" ", ToStrings(ForbiddenNameCharacters))}).";
            if (LooksLikeSid(pattern))
                return $"\"{raw}\" is a SID — enter the account name instead.";
            if (IsTooBroad(pattern))
                return $"\"{raw}\" is too broad: a pattern needs at least {MinPatternLiterals} characters besides * and ?.";
            return null;
        }

        private static bool LooksLikeSid(string pattern)
            => pattern.Length > 4
               && pattern.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase)
               && char.IsDigit(pattern[4]);

        private static IEnumerable<string> ToStrings(string characters)
        {
            foreach (var ch in characters)
                yield return ch.ToString();
        }
    }
}
