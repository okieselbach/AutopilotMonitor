using System;
using System.IO;
using System.Net.Http;
using System.Reflection;
using AutopilotMonitor.Agent.V2.Core.Configuration;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Transport;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Shared.Models;
using Newtonsoft.Json;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Configuration
{
    /// <summary>
    /// Pins the OOBE-MITM cache-hardening contract on <see cref="RemoteConfigService"/>
    /// (RemoteConfigService.cs:234-311). The on-disk config cache MUST NOT persist — nor,
    /// on read, trust — security-sensitive fields: a single MITM during OOBE could otherwise
    /// plant attacker-controlled values (UnrestrictedMode / AllowAgentDowngrade /
    /// LatestAgentExeSha256 / DeviceBlocked / DeviceKillSignal / UnblockAt) that survive every
    /// future cold boot. The strip is defence-in-depth on BOTH paths — <c>CacheConfig</c> (write)
    /// and <c>LoadCachedConfig</c> (read) — so both are asserted here.
    /// <para>
    /// The production cache path is a fixed <c>%ProgramData%\AutopilotMonitor\Config</c> location.
    /// To exercise the REAL <c>CacheConfig</c>/<c>LoadCachedConfig</c> disk + serialization code in
    /// isolation, the probe subclass exposes the two protected methods and the private
    /// <c>_cacheFilePath</c> field is redirected to a per-test <see cref="TempDirectory"/> via
    /// reflection. Nothing about the strip logic itself is faked.
    /// </para>
    /// </summary>
    public sealed class RemoteConfigServiceCacheHardeningTests : IDisposable
    {
        private readonly TempDirectory _tmp = new TempDirectory();
        private readonly AgentLogger _logger;
        private readonly string _cachePath;

        public RemoteConfigServiceCacheHardeningTests()
        {
            _logger = new AgentLogger(Path.Combine(_tmp.Path, "logs"), AgentLogLevel.Info);
            _cachePath = Path.Combine(_tmp.Path, "remote-config.json");
        }

        public void Dispose() => _tmp.Dispose();

        // ── Write path: CacheConfig strips before serializing to disk ────────

        [Fact]
        public void CacheConfig_strips_all_security_sensitive_fields_on_disk()
        {
            var probe = NewProbe();

            // A config carrying a full set of attacker-controlled security values, as if a live
            // (or MITM'd) fetch returned them. Benign fields are set to non-default markers so the
            // round-trip can prove they are NOT collateral-stripped.
            var config = new AgentConfigResponse
            {
                ConfigVersion = 77,
                NtpServer = "pool.attacker.example",
                UnrestrictedMode = true,
                AllowAgentDowngrade = true,
                LatestAgentExeSha256 = "deadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeef",
                DeviceBlocked = true,
                DeviceKillSignal = true,
                UnblockAt = new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            };

            probe.DoCache(config);

            // Read the file the production code actually wrote and deserialize with the same lib.
            var raw = File.ReadAllText(_cachePath);
            var persisted = JsonConvert.DeserializeObject<AgentConfigResponse>(raw);

            Assert.False(persisted.UnrestrictedMode);
            Assert.False(persisted.AllowAgentDowngrade);
            Assert.Null(persisted.LatestAgentExeSha256);
            Assert.False(persisted.DeviceBlocked);
            Assert.False(persisted.DeviceKillSignal);
            Assert.Null(persisted.UnblockAt);

            // The attacker EXE hash must not appear anywhere in the persisted bytes.
            Assert.DoesNotContain("deadbeef", raw);

            // Benign fields survive the strip.
            Assert.Equal(77, persisted.ConfigVersion);
            Assert.Equal("pool.attacker.example", persisted.NtpServer);

            // The in-memory object is restored after serialization — CacheConfig must not
            // permanently mutate the live config it was handed (it is still in use by the agent).
            Assert.True(config.UnrestrictedMode);
            Assert.True(config.AllowAgentDowngrade);
            Assert.Equal("deadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeef", config.LatestAgentExeSha256);
            Assert.True(config.DeviceBlocked);
            Assert.True(config.DeviceKillSignal);
            Assert.Equal(new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc), config.UnblockAt);
        }

        // ── Read path: LoadCachedConfig strips a planted (attacker) cache file ─

        [Theory]
        [InlineData("UnrestrictedMode")]
        [InlineData("AllowAgentDowngrade")]
        [InlineData("LatestAgentExeSha256")]
        [InlineData("DeviceBlocked")]
        [InlineData("DeviceKillSignal")]
        [InlineData("UnblockAt")]
        public void LoadCachedConfig_strips_planted_security_sensitive_field(string field)
        {
            var probe = NewProbe();

            // Simulate an OOBE-MITM (or an older cache format) having persisted a cache file that
            // DOES carry attacker values on every sensitive field — written out-of-band, bypassing
            // the write-side strip. The read-side strip is the defence-in-depth that must still hold.
            var attacker = new AgentConfigResponse
            {
                ConfigVersion = 5,
                UnrestrictedMode = true,
                AllowAgentDowngrade = true,
                LatestAgentExeSha256 = "0011223344556677889900112233445566778899001122334455667788990011",
                DeviceBlocked = true,
                DeviceKillSignal = true,
                UnblockAt = new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            };
            File.WriteAllText(_cachePath, JsonConvert.SerializeObject(attacker, Formatting.Indented));

            var loaded = probe.DoLoad();

            Assert.NotNull(loaded);
            switch (field)
            {
                case "UnrestrictedMode":
                    Assert.False(loaded.UnrestrictedMode);
                    break;
                case "AllowAgentDowngrade":
                    Assert.False(loaded.AllowAgentDowngrade);
                    break;
                case "LatestAgentExeSha256":
                    Assert.Null(loaded.LatestAgentExeSha256);
                    break;
                case "DeviceBlocked":
                    Assert.False(loaded.DeviceBlocked);
                    break;
                case "DeviceKillSignal":
                    Assert.False(loaded.DeviceKillSignal);
                    break;
                case "UnblockAt":
                    Assert.Null(loaded.UnblockAt);
                    break;
                default:
                    throw new InvalidOperationException($"unknown field {field}");
            }

            // Benign data from the planted file is still loaded — the strip is surgical.
            Assert.Equal(5, loaded.ConfigVersion);
        }

        // ── Round-trip: benign fields survive write + read unchanged ─────────

        [Fact]
        public void RoundTrip_preserves_non_sensitive_fields()
        {
            var probe = NewProbe();

            var config = new AgentConfigResponse
            {
                ConfigVersion = 26,
                UploadIntervalSeconds = 45,
                MaxBatchSize = 250,
                SelfDestructOnComplete = false,
                KeepLogFile = true,
                EnableGeoLocation = false,
                NtpServer = "time.contoso.com",
                LogLevel = "Debug",
                DiagnosticsUploadMode = "OnFailure",
                // A non-sensitive hash field (ZIP hash) that must NOT be stripped — only the
                // EXE hash (LatestAgentExeSha256) is security-sensitive on the cache path.
                LatestAgentSha256 = "cafebabecafebabecafebabecafebabecafebabecafebabecafebabecafebabe",
            };

            probe.DoCache(config);
            var loaded = probe.DoLoad();

            Assert.NotNull(loaded);
            Assert.Equal(26, loaded.ConfigVersion);
            Assert.Equal(45, loaded.UploadIntervalSeconds);
            Assert.Equal(250, loaded.MaxBatchSize);
            Assert.False(loaded.SelfDestructOnComplete);
            Assert.True(loaded.KeepLogFile);
            Assert.False(loaded.EnableGeoLocation);
            Assert.Equal("time.contoso.com", loaded.NtpServer);
            Assert.Equal("Debug", loaded.LogLevel);
            Assert.Equal("OnFailure", loaded.DiagnosticsUploadMode);
            Assert.Equal("cafebabecafebabecafebabecafebabecafebabecafebabecafebabecafebabe", loaded.LatestAgentSha256);
        }

        // ── Helpers ─────────────────────────────────────────────────────────

        private CacheProbe NewProbe()
        {
            // A throwaway BackendApiClient — the cache-path methods never hit the wire.
            var apiClient = new BackendApiClient(
                httpClient: new HttpClient(),
                baseUrl: "http://localhost",
                manufacturer: string.Empty,
                model: string.Empty,
                serialNumber: string.Empty,
                useBootstrapTokenAuth: false,
                bootstrapToken: null,
                agentVersion: "0.0.0",
                logger: _logger);

            var probe = new CacheProbe(apiClient, _logger);
            RedirectCachePath(probe, _cachePath);
            return probe;
        }

        // Redirect the private, constructor-computed %ProgramData% cache path to the per-test temp
        // file so the REAL CacheConfig/LoadCachedConfig disk code runs in isolation.
        private static void RedirectCachePath(RemoteConfigService svc, string path)
        {
            var field = typeof(RemoteConfigService).GetField(
                "_cacheFilePath", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field); // guards against a rename silently disabling the redirect
            field.SetValue(svc, path);
        }

        // Minimal subclass: exposes the two protected cache seams so the real strip logic can be
        // driven directly, without standing up the retry/fetch machinery.
        private sealed class CacheProbe : RemoteConfigService
        {
            public CacheProbe(BackendApiClient apiClient, AgentLogger logger)
                : base(apiClient, "00000000-0000-0000-0000-000000000001", logger)
            {
            }

            public void DoCache(AgentConfigResponse config) => CacheConfig(config);
            public AgentConfigResponse DoLoad() => LoadCachedConfig();
        }
    }
}
