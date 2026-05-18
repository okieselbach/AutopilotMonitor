using System;

namespace AutopilotMonitor.Functions.Services.Offboarding
{
    /// <summary>
    /// Thrown by <see cref="SafeWipeService"/> when the verify step finds rows in the fetched
    /// set that do not match the expected tenant anchor (PartitionKey, TenantId-Property, or
    /// blob-name prefix depending on variant). NO DATA IS DELETED at this point — the wipe is
    /// aborted before any delete call so a misbehaving filter cannot cause cross-tenant data
    /// loss. Operator must inspect the unexpected rows manually.
    /// </summary>
    public class SafeWipeVerificationException : Exception
    {
        public string TableOrContainer { get; }
        public string ExpectedAnchor { get; }
        public int MismatchCount { get; }

        public SafeWipeVerificationException(string tableOrContainer, string expectedAnchor, int mismatchCount)
            : base($"SafeWipe verify aborted for '{tableOrContainer}': {mismatchCount} row(s) " +
                   $"did not match expected anchor '{expectedAnchor}'. No deletes were issued. " +
                   $"Manual review required — possible filter bug or schema drift.")
        {
            TableOrContainer = tableOrContainer;
            ExpectedAnchor = expectedAnchor;
            MismatchCount = mismatchCount;
        }
    }
}
