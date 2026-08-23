using System;
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.DecisionCore.Signals;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Orchestration
{
    /// <summary>
    /// Test fake — captures every synthetic signal posted by the EffectRunner.
    /// </summary>
    internal sealed class FakeSignalIngressSink : ISignalIngressSink
    {
        private readonly List<PostedSignal> _posted = new List<PostedSignal>();
        private readonly object _gate = new object();

        /// <summary>
        /// Snapshot, not the live list: producers post from timer / worker threads (e.g. the
        /// parked-dwell tripwire) while tests poll this in a SpinWait loop. Enumerating the live
        /// list across an Add throws "Collection was modified" — a rare CI-only failure
        /// (run 32644425162) with no assertion text.
        /// </summary>
        public IReadOnlyList<PostedSignal> Posted
        {
            get { lock (_gate) return _posted.ToArray(); }
        }

        /// <summary>When non-null, <see cref="Post"/> throws instead of capturing.</summary>
        public Exception? ThrowOnPost { get; set; }

        public void Post(
            DecisionSignalKind kind,
            DateTime occurredAtUtc,
            string sourceOrigin,
            Evidence evidence,
            IReadOnlyDictionary<string, string>? payload = null,
            int kindSchemaVersion = 1,
            object? typedPayload = null)
        {
            if (ThrowOnPost != null) throw ThrowOnPost;
            lock (_gate)
                _posted.Add(new PostedSignal(kind, occurredAtUtc, sourceOrigin, evidence, payload, kindSchemaVersion, typedPayload));
        }

        internal sealed class PostedSignal
        {
            public PostedSignal(
                DecisionSignalKind kind,
                DateTime occurredAtUtc,
                string sourceOrigin,
                Evidence evidence,
                IReadOnlyDictionary<string, string>? payload,
                int kindSchemaVersion,
                object? typedPayload)
            {
                Kind = kind;
                OccurredAtUtc = occurredAtUtc;
                SourceOrigin = sourceOrigin;
                Evidence = evidence;
                Payload = payload;
                KindSchemaVersion = kindSchemaVersion;
                TypedPayload = typedPayload;
            }

            public DecisionSignalKind Kind { get; }
            public DateTime OccurredAtUtc { get; }
            public string SourceOrigin { get; }
            public Evidence Evidence { get; }
            public IReadOnlyDictionary<string, string>? Payload { get; }
            public int KindSchemaVersion { get; }
            public object? TypedPayload { get; }
        }
    }
}
