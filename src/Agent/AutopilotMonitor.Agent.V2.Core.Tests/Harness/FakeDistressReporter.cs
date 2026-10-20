using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Transport;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Harness
{
    /// <summary>
    /// Records every distress dispatch instead of sending it. The real anti-flood rules of
    /// <see cref="DistressReporter"/> are bypassed on purpose: tests assert what the caller dispatched.
    /// </summary>
    internal sealed class FakeDistressReporter : DistressReporter
    {
        public sealed class Call
        {
            public DistressErrorType ErrorType { get; set; }
            public string Message { get; set; } = default!;
            public int? HttpStatusCode { get; set; }
            public string? TpmVersion { get; set; }
        }

        public List<Call> Calls { get; } = new List<Call>();

        /// <summary>
        /// The send each dispatch returns; null = already completed. Lets a test keep a send in
        /// flight to prove a caller waits for it (or stops waiting at its bound).
        /// </summary>
        public Func<Task>? Send { get; set; }

        public FakeDistressReporter()
            : base(baseUrl: "https://example.test", tenantId: "t", manufacturer: "m", model: "d", serialNumber: "s", agentVersion: "0.0.0", logger: null!)
        {
        }

        public override Task TrySendAsync(DistressErrorType errorType, string message, int? httpStatusCode = null, string? tpmVersion = null)
        {
            Calls.Add(new Call { ErrorType = errorType, Message = message, HttpStatusCode = httpStatusCode, TpmVersion = tpmVersion });
            return Send?.Invoke() ?? Task.CompletedTask;
        }
    }
}
