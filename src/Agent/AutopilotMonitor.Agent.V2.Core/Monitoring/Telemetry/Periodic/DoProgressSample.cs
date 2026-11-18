namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Periodic
{
    /// <summary>
    /// One Delivery Optimization poll result for a tracked app whose bytes changed. The collector
    /// hands it to the download-progress publisher, which decides whether it becomes a
    /// <c>download_progress</c> event.
    /// </summary>
    public sealed class DoProgressSample
    {
        public DoProgressSample(
            long totalBytes, long fileSize,
            long bytesFromPeers, long bytesFromHttp, int percentPeerCaching, int downloadMode,
            long bytesFromLanPeers, long bytesFromGroupPeers, long bytesFromInternetPeers,
            long bytesFromLinkLocalPeers, long bytesFromCacheServer, string cacheHost)
        {
            TotalBytes = totalBytes;
            FileSize = fileSize;
            BytesFromPeers = bytesFromPeers;
            BytesFromHttp = bytesFromHttp;
            PercentPeerCaching = percentPeerCaching;
            DownloadMode = downloadMode;
            BytesFromLanPeers = bytesFromLanPeers;
            BytesFromGroupPeers = bytesFromGroupPeers;
            BytesFromInternetPeers = bytesFromInternetPeers;
            BytesFromLinkLocalPeers = bytesFromLinkLocalPeers;
            BytesFromCacheServer = bytesFromCacheServer;
            CacheHost = cacheHost;
        }

        public long TotalBytes { get; }
        public long FileSize { get; }
        public long BytesFromPeers { get; }
        public long BytesFromHttp { get; }
        public int PercentPeerCaching { get; }
        public int DownloadMode { get; }
        public long BytesFromLanPeers { get; }
        public long BytesFromGroupPeers { get; }
        public long BytesFromInternetPeers { get; }
        public long BytesFromLinkLocalPeers { get; }
        public long BytesFromCacheServer { get; }

        /// <summary>CacheHost of the Connected Cache node; null when no cache served bytes.</summary>
        public string CacheHost { get; }

        public int PercentComplete => FileSize > 0 ? (int)((TotalBytes * 100) / FileSize) : 0;
    }
}
