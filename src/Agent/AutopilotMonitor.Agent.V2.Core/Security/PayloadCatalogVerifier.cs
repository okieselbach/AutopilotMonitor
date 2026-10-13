using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using AutopilotMonitor.Shared;

namespace AutopilotMonitor.Agent.V2.Core.Security
{
    /// <summary>
    /// Verifies an extracted agent payload against its signed catalog
    /// (<see cref="Constants.AgentPayloadCatalogFileName"/>) with Windows PowerShell's own
    /// <c>Test-FileCatalog</c>, the cmdlet the bootstrap installer and the OOBE script run as well,
    /// so all three verifiers share one semantics: the catalog must carry a valid signature from
    /// <see cref="Constants.CodeSigningPublisherPattern"/>, and every file of the payload must match
    /// it, with nothing missing, changed or extra.
    /// <para>
    /// A reimplementation of the catalog format (WinVerifyTrust plus the CryptCAT APIs) was the
    /// alternative and was rejected: a second parser for the same contract would drift from the
    /// cmdlet the other two verifiers use. The runspace costs about a second, paid only on the
    /// rare update path.
    /// </para>
    /// </summary>
    public static class PayloadCatalogVerifier
    {
        private const int InvokeTimeoutMs = 60000;
        private const int MaxNamesPerList = 5;

        /// <summary>
        /// Checks <paramref name="payloadDir"/>. Never throws: any failure to run the check is a
        /// rejection, because an unverifiable payload must not be installed.
        /// </summary>
        public static PayloadCatalogVerdict Verify(string payloadDir)
        {
            var catalogPath = Path.Combine(payloadDir, Constants.AgentPayloadCatalogFileName);
            if (!File.Exists(catalogPath))
                return PayloadCatalogVerdict.Reject($"catalog {Constants.AgentPayloadCatalogFileName} missing");

            return RunCatalogCheck(payloadDir, catalogPath);
        }

        // Kept out of Verify (and never inlined) so the PowerShell engine is loaded only when a
        // catalog is actually checked, not when the method that guards it is compiled.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static PayloadCatalogVerdict RunCatalogCheck(string payloadDir, string catalogPath)
        {
            try
            {
                using (var runspace = RunspaceFactory.CreateRunspace())
                {
                    runspace.Open();
                    using (var ps = PowerShell.Create())
                    {
                        ps.Runspace = runspace;
                        ps.AddCommand(@"Microsoft.PowerShell.Security\Test-FileCatalog")
                          .AddParameter("Path", payloadDir)
                          .AddParameter("CatalogFilePath", catalogPath)
                          .AddParameter("FilesToSkip", new[] { Constants.AgentPayloadCatalogFileName })
                          .AddParameter("Detailed");

                        var asyncResult = ps.BeginInvoke();
                        if (!asyncResult.AsyncWaitHandle.WaitOne(InvokeTimeoutMs))
                        {
                            ps.Stop();
                            return PayloadCatalogVerdict.Reject($"catalog check timed out after {InvokeTimeoutMs / 1000}s");
                        }

                        var results = ps.EndInvoke(asyncResult);
                        if (ps.HadErrors && ps.Streams.Error.Count > 0)
                            return PayloadCatalogVerdict.Reject($"catalog check failed to run ({ps.Streams.Error[0]})");

                        var info = results.FirstOrDefault();
                        if (info == null)
                            return PayloadCatalogVerdict.Reject("catalog check returned nothing");

                        var signature = info.Properties["Signature"]?.Value as Signature;
                        return Evaluate(new PayloadCatalogFacts(
                            status: info.Properties["Status"]?.Value?.ToString(),
                            signatureStatus: signature?.Status.ToString(),
                            signerSubject: signature?.SignerCertificate?.Subject,
                            catalogItems: ToStringMap(info.Properties["CatalogItems"]?.Value),
                            pathItems: ToStringMap(info.Properties["PathItems"]?.Value)),
                            Constants.CodeSigningPublisherPattern);
                    }
                }
            }
            catch (Exception ex)
            {
                return PayloadCatalogVerdict.Reject($"catalog check failed to run ({ex.Message})");
            }
        }

        /// <summary>
        /// The decision over the facts <c>Test-FileCatalog -Detailed</c> reports. Separate from the
        /// runspace so it is testable without PowerShell. <c>Status</c> covers the hashes only: an
        /// unsigned catalog over matching files reports <c>Valid</c>, which is why the signature is
        /// judged on its own.
        /// </summary>
        internal static PayloadCatalogVerdict Evaluate(PayloadCatalogFacts facts, string publisherPattern)
        {
            var reasons = new List<string>();

            if (!string.Equals(facts.SignatureStatus, "Valid", StringComparison.Ordinal))
            {
                reasons.Add($"catalog signature status {facts.SignatureStatus ?? "unknown"}");
            }
            else if (!MatchesPublisherPattern(facts.SignerSubject, publisherPattern))
            {
                reasons.Add($"catalog publisher '{facts.SignerSubject}' does not match {publisherPattern}");
            }

            if (!string.Equals(facts.Status, "Valid", StringComparison.Ordinal))
                reasons.Add(DescribeDifference(facts.CatalogItems, facts.PathItems));

            return reasons.Count == 0
                ? PayloadCatalogVerdict.Accept()
                : PayloadCatalogVerdict.Reject(string.Join("; ", reasons));
        }

        /// <summary>
        /// PowerShell's case-insensitive <c>-like</c> for the two wildcards the pin uses (<c>*</c>,
        /// <c>?</c>), without loading the PowerShell engine. Bracket classes are not supported;
        /// <c>CodeSigningParityTests</c> pins the pattern's shape.
        /// </summary>
        internal static bool MatchesPublisherPattern(string subject, string pattern)
        {
            if (string.IsNullOrEmpty(subject) || string.IsNullOrEmpty(pattern)) return false;
            var regex = "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
            return Regex.IsMatch(subject, regex, RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
        }

        private static string DescribeDifference(
            IReadOnlyDictionary<string, string> expected, IReadOnlyDictionary<string, string> actual)
        {
            var missing = expected.Keys.Where(k => !actual.ContainsKey(k));
            var extra = actual.Keys.Where(k => !expected.ContainsKey(k));
            var changed = expected.Keys.Where(k => actual.TryGetValue(k, out var hash)
                                                   && !string.Equals(hash, expected[k], StringComparison.OrdinalIgnoreCase));
            return $"payload differs from catalog (missing: {Cap(missing)}; extra: {Cap(extra)}; changed: {Cap(changed)})";
        }

        private static string Cap(IEnumerable<string> names)
        {
            var list = names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            if (list.Count == 0) return "none";
            var shown = string.Join(", ", list.Take(MaxNamesPerList));
            return list.Count > MaxNamesPerList ? $"{shown} (+{list.Count - MaxNamesPerList} more)" : shown;
        }

        private static IReadOnlyDictionary<string, string> ToStringMap(object value)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (value is IDictionary dictionary)
            {
                foreach (DictionaryEntry entry in dictionary)
                    map[Convert.ToString(entry.Key)] = Convert.ToString(entry.Value);
            }
            return map;
        }
    }

    /// <summary>What <c>Test-FileCatalog -Detailed</c> reported, reduced to the fields the decision needs.</summary>
    internal sealed class PayloadCatalogFacts
    {
        public PayloadCatalogFacts(
            string status, string signatureStatus, string signerSubject,
            IReadOnlyDictionary<string, string> catalogItems, IReadOnlyDictionary<string, string> pathItems)
        {
            Status = status;
            SignatureStatus = signatureStatus;
            SignerSubject = signerSubject;
            CatalogItems = catalogItems ?? new Dictionary<string, string>();
            PathItems = pathItems ?? new Dictionary<string, string>();
        }

        public string Status { get; }
        public string SignatureStatus { get; }
        public string SignerSubject { get; }
        public IReadOnlyDictionary<string, string> CatalogItems { get; }
        public IReadOnlyDictionary<string, string> PathItems { get; }
    }

    /// <summary>Outcome of a payload catalog check; <see cref="Reason"/> is null when accepted.</summary>
    public sealed class PayloadCatalogVerdict
    {
        private PayloadCatalogVerdict(bool accepted, string reason)
        {
            Accepted = accepted;
            Reason = reason;
        }

        public bool Accepted { get; }
        public string Reason { get; }

        public static PayloadCatalogVerdict Accept() => new PayloadCatalogVerdict(true, null);

        public static PayloadCatalogVerdict Reject(string reason) => new PayloadCatalogVerdict(false, reason);
    }
}
