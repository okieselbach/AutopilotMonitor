namespace AutopilotMonitor.Shared.Models
{
    /// <summary>What a device block does once the agent receives it.</summary>
    public static class DeviceBlockActions
    {
        /// <summary>The session's uploads pause until the block ends; the agent keeps running.</summary>
        public const string Block = "Block";

        /// <summary>
        /// The agent ends monitoring and removes itself; until the block ends, every agent on the
        /// device does the same, new enrollments included.
        /// </summary>
        public const string Kill = "Kill";

        public static readonly string[] All = { Block, Kill };
    }

    /// <summary>Why the platform blocked a session (<see cref="SessionSummary.BlockCause"/>).</summary>
    public static class SessionBlockCauses
    {
        /// <summary>The session exceeded the platform's event limit (maintenance auto-action).</summary>
        public const string EventLimit = "event_limit";

        /// <summary>The platform operator blocked the device for this session.</summary>
        public const string Operator = "operator";

        public static readonly string[] All = { EventLimit, Operator };
    }
}
