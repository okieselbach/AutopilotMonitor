using System;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Install channels an <see cref="AppInstallSummary"/> row can come from. The value is part of
    /// the row identity and of every per-app aggregation key, so an Intune app and a RealmJoin
    /// package with the same display name never merge into one row or one app group.
    /// </summary>
    public static class AppInstallSources
    {
        /// <summary>Intune Management Extension apps, parsed from the IME logs.</summary>
        public const string Ime = "ime";

        /// <summary>RealmJoin agent packages, observed in the RealmJoin package registry.</summary>
        public const string RealmJoin = "realmjoin";

        public static readonly string[] All = { Ime, RealmJoin };

        /// <summary>
        /// The stored column read as a source: empty is <see cref="Ime"/> (rows written before the
        /// column existed were all IME rows).
        /// </summary>
        public static string Normalize(string? source) => string.IsNullOrEmpty(source) ? Ime : source!;

        public static bool IsKnown(string? source) => Array.IndexOf(All, source) >= 0;
    }
}
