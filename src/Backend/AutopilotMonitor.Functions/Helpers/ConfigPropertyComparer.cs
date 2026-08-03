using System;
using System.Collections.Generic;
using System.Reflection;

namespace AutopilotMonitor.Functions.Helpers
{
    /// <summary>
    /// Precise, unmasked property-level comparison of two config objects. Returns the NAMES
    /// of every changed property (values never leave this method), excluding only the table
    /// plumbing keys — unlike <see cref="ConfigDiffHelper"/>, LastUpdated/UpdatedBy/TenantId
    /// ARE visible, because callers (backup noise filter, transactional write verification)
    /// must see every real difference.
    /// </summary>
    public static class ConfigPropertyComparer
    {
        private static readonly HashSet<string> ExcludedProperties = new(StringComparer.OrdinalIgnoreCase)
        {
            "PartitionKey", "RowKey", "Timestamp", "ETag",
        };

        public static HashSet<string> GetChangedPropertyNames<T>(T a, T b) where T : class
        {
            var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (a == null || b == null) throw new ArgumentNullException(a == null ? nameof(a) : nameof(b));

            foreach (var prop in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!prop.CanRead) continue;
                if (prop.GetIndexParameters().Length > 0) continue;
                if (ExcludedProperties.Contains(prop.Name)) continue;

                if (!Equals(prop.GetValue(a), prop.GetValue(b)))
                    changed.Add(prop.Name);
            }

            return changed;
        }
    }
}
