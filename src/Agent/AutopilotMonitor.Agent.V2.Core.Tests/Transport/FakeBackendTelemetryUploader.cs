#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Transport.Telemetry;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Transport
{
    /// <summary>
    /// Scripted test double for <see cref="IBackendTelemetryUploader"/>. Feed it a sequence of
    /// <see cref="UploadResult"/>s and it returns them in order. Tracks every batch it was called with.
    /// </summary>
    internal sealed class FakeBackendTelemetryUploader : IBackendTelemetryUploader
    {
        private readonly Queue<Func<IReadOnlyList<TelemetryItem>, UploadResult>> _script =
            new Queue<Func<IReadOnlyList<TelemetryItem>, UploadResult>>();

        private readonly ConcurrentQueue<IReadOnlyList<TelemetryItem>> _received =
            new ConcurrentQueue<IReadOnlyList<TelemetryItem>>();

        public int CallCount { get; private set; }

        public IReadOnlyList<IReadOnlyList<TelemetryItem>> Received
        {
            get
            {
                var list = new List<IReadOnlyList<TelemetryItem>>();
                foreach (var b in _received) list.Add(b);
                return list;
            }
        }

        public FakeBackendTelemetryUploader QueueOk(int times = 1)
        {
            for (int i = 0; i < times; i++) _script.Enqueue(_ => UploadResult.Ok());
            return this;
        }

        public FakeBackendTelemetryUploader QueueTransient(string reason = "fake-transient")
        {
            _script.Enqueue(_ => UploadResult.Transient(reason));
            return this;
        }

        public FakeBackendTelemetryUploader QueuePermanent(string reason = "fake-permanent")
        {
            _script.Enqueue(_ => UploadResult.Permanent(reason));
            return this;
        }

        public FakeBackendTelemetryUploader QueueUnauthorized(string reason = "fake-unauthorized")
        {
            _script.Enqueue(_ => UploadResult.Unauthorized(reason));
            return this;
        }

        public FakeBackendTelemetryUploader QueueTooLarge(string reason = "fake-413")
        {
            _script.Enqueue(_ => UploadResult.TooLarge(reason));
            return this;
        }

        public FakeBackendTelemetryUploader QueuePoison(string[] rejectedRowKeys, string reason = "fake-poison")
        {
            _script.Enqueue(_ => UploadResult.Poison(rejectedRowKeys, reason));
            return this;
        }

        public FakeBackendTelemetryUploader QueueSessionGone(string reason = "session gone: http 410")
        {
            _script.Enqueue(_ => UploadResult.SessionGone(reason));
            return this;
        }

        public FakeBackendTelemetryUploader QueueThrow(Exception ex)
        {
            _script.Enqueue(_ => throw ex);
            return this;
        }

        /// <summary>
        /// Queues a 2xx success carrying backend-to-agent control signals (DeviceBlocked /
        /// DeviceKillSignal / AdminAction / Actions). M4.6.ε.
        /// </summary>
        public FakeBackendTelemetryUploader QueueOkWithSignals(
            bool deviceBlocked = false,
            DateTime? unblockAt = null,
            bool deviceKillSignal = false,
            string? reason_AdminAction = null,
            IReadOnlyList<AutopilotMonitor.Shared.Models.ServerAction>? actions = null)
        {
            _script.Enqueue(_ => UploadResult.OkWithSignals(
                deviceBlocked: deviceBlocked,
                unblockAt: unblockAt,
                deviceKillSignal: deviceKillSignal,
                adminAction: reason_AdminAction,
                actions: actions));
            return this;
        }

        public Task<UploadResult> UploadBatchAsync(
            IReadOnlyList<TelemetryItem> items,
            CancellationToken cancellationToken)
        {
            CallCount++;
            _received.Enqueue(items);

            if (_script.Count == 0)
            {
                return Task.FromResult(UploadResult.Transient("fake-no-script"));
            }

            var next = _script.Dequeue();
            return Task.FromResult(next(items));
        }
    }
}
