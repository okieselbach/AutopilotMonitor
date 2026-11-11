#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Security
{
    /// <summary>
    /// Counts console processes that were gone before they could be inspected — script children,
    /// reported as one summary per window instead of one event each. Thread-safe. The content
    /// leaves through <see cref="Take"/>, which hands out copies and leaves an empty tally behind,
    /// so a start is never counted in two summaries.
    /// </summary>
    internal sealed class ConsoleScriptActivityTally
    {
        internal const int MaxOwnersListed = 5;
        internal const int MaxParentsListed = 10;
        internal const int MaxParentLookups = 10;

        // A spawner can keep running for hours after the host has stopped opening windows. The
        // count grows with it; what is remembered per start does not.
        private const int MaxTrackedParents = 256;
        private const int MaxTrackedProcessIds = 2048;
        private const int MaxTrackedOwners = 64;

        private sealed class Parent
        {
            public string? Name;
            public int Children;
        }

        private readonly object _lock = new object();
        private readonly SortedSet<int> _sessions = new SortedSet<int>();
        private readonly List<string> _owners = new List<string>();
        private readonly Dictionary<int, Parent> _parents = new Dictionary<int, Parent>();
        private readonly HashSet<int> _processIds = new HashSet<int>();
        private string _processName = string.Empty;
        private int _count;
        private int _parentLookups;
        private DateTime _firstSeenUtc;
        private DateTime _lastSeenUtc;

        /// <summary>
        /// Counts one start. <paramref name="parentName"/> is asked at most once per parent and at
        /// most <see cref="MaxParentLookups"/> times per window, outside the lock; a parent that is
        /// itself one of the counted processes is named without asking.
        /// </summary>
        /// <returns><c>true</c> when this start opened an empty tally.</returns>
        public bool Add(
            string processName, int processId, int parentProcessId, int sessionId, string? ownerSid,
            DateTime seenUtc, Func<int, string?> parentName)
        {
            bool opened;
            Parent? toResolve = null;
            lock (_lock)
            {
                opened = _count == 0;
                if (opened)
                {
                    _processName = processName;
                    _firstSeenUtc = seenUtc;
                }
                _count++;
                _lastSeenUtc = seenUtc;
                _sessions.Add(sessionId);

                if (!string.IsNullOrEmpty(ownerSid) && _owners.Count < MaxTrackedOwners && !_owners.Contains(ownerSid!))
                    _owners.Add(ownerSid!);

                if (parentProcessId > 0)
                {
                    if (_parents.TryGetValue(parentProcessId, out var parent))
                    {
                        parent.Children++;
                    }
                    else if (_parents.Count < MaxTrackedParents)
                    {
                        parent = new Parent { Children = 1 };
                        _parents[parentProcessId] = parent;
                        if (_processIds.Contains(parentProcessId))
                        {
                            parent.Name = processName;
                        }
                        else if (_parentLookups < MaxParentLookups)
                        {
                            _parentLookups++;
                            toResolve = parent;
                        }
                    }
                }

                if (_processIds.Count < MaxTrackedProcessIds) _processIds.Add(processId);
            }

            if (toResolve != null)
            {
                var name = parentName(parentProcessId);
                lock (_lock) { toResolve.Name = name; }
            }

            return opened;
        }

        /// <summary>The counted content, or <c>null</c> when nothing was counted. Empties the tally.</summary>
        public ConsoleScriptActivity? Take()
        {
            lock (_lock)
            {
                if (_count == 0) return null;

                var parents = _parents
                    .Select(kv => new ConsoleScriptParent(kv.Key, kv.Value.Name, kv.Value.Children))
                    .OrderByDescending(p => p.Children)
                    .ThenBy(p => p.ProcessId)
                    .ToList();

                var activity = new ConsoleScriptActivity(
                    _processName, _count, _firstSeenUtc, _lastSeenUtc,
                    _sessions.ToList(),
                    _owners.Take(MaxOwnersListed).ToList(), _owners.Count,
                    parents.Take(MaxParentsListed).ToList(), parents.Count);

                _count = 0;
                _parentLookups = 0;
                _processName = string.Empty;
                _sessions.Clear();
                _owners.Clear();
                _parents.Clear();
                _processIds.Clear();
                return activity;
            }
        }
    }

    /// <summary>One window of counted console processes. Lists are capped; the totals are not.</summary>
    internal sealed class ConsoleScriptActivity
    {
        public ConsoleScriptActivity(
            string processName, int count, DateTime firstSeenUtc, DateTime lastSeenUtc,
            IReadOnlyList<int> windowsSessions,
            IReadOnlyList<string> owners, int ownersTotal,
            IReadOnlyList<ConsoleScriptParent> parents, int parentsTotal)
        {
            ProcessName = processName;
            Count = count;
            FirstSeenUtc = firstSeenUtc;
            LastSeenUtc = lastSeenUtc;
            WindowsSessions = windowsSessions;
            Owners = owners;
            OwnersTotal = ownersTotal;
            Parents = parents;
            ParentsTotal = parentsTotal;
        }

        public string ProcessName { get; }
        public int Count { get; }
        public DateTime FirstSeenUtc { get; }
        public DateTime LastSeenUtc { get; }
        public IReadOnlyList<int> WindowsSessions { get; }
        public IReadOnlyList<string> Owners { get; }
        public int OwnersTotal { get; }
        /// <summary>Parents by number of children, most first.</summary>
        public IReadOnlyList<ConsoleScriptParent> Parents { get; }
        public int ParentsTotal { get; }
    }

    /// <summary>A parent process of counted console processes. Only its image name is kept, never its command line.</summary>
    internal sealed class ConsoleScriptParent
    {
        public ConsoleScriptParent(int processId, string? name, int children)
        {
            ProcessId = processId;
            Name = name;
            Children = children;
        }

        public int ProcessId { get; }
        /// <summary><c>null</c> when the parent had exited or was not looked up.</summary>
        public string? Name { get; }
        public int Children { get; }
    }
}
