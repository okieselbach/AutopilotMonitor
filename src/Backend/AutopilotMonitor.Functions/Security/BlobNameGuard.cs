namespace AutopilotMonitor.Functions.Security
{
    /// <summary>
    /// Last-line guard for request-derived blob names. Azure.Storage.Blobs appends the name to
    /// the container URI preserving '/' and System.Uri collapses '..' segments, so a name such as
    /// "x/../../other-container/y" resolves OUTSIDE the intended container. Every blob name that
    /// contains caller-influenced material must be flat: no separators, no dot segments.
    /// </summary>
    public static class BlobNameGuard
    {
        public static bool IsFlat(string? blobName)
        {
            if (string.IsNullOrWhiteSpace(blobName)) return false;
            if (blobName.Contains('/') || blobName.Contains('\\')) return false;
            if (blobName.Contains("..")) return false;
            if (blobName.Contains('%') || blobName.Contains('?') || blobName.Contains('#')) return false;
            return true;
        }

        /// <summary>Throws <see cref="ArgumentException"/> when the name is not flat.</summary>
        public static string EnsureFlat(string? blobName, string parameterName)
        {
            if (!IsFlat(blobName))
                throw new ArgumentException("Blob name must be a flat name without path separators or dot segments.", parameterName);
            return blobName!;
        }
    }
}
