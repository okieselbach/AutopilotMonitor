using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using Azure;
using Azure.Data.Tables;
using Azure.Data.Tables.Models;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests
{
    public class StartupTelemetryServiceTests
    {
        private sealed class CapturingChannel : ITelemetryChannel
        {
            public List<ITelemetry> Items { get; } = new();
            public bool? DeveloperMode { get; set; }
            public string EndpointAddress { get; set; } = "";
            public void Send(ITelemetry item) => Items.Add(item);
            public void Flush() { }
            public void Dispose() { }
        }

        private sealed class Lifetime : IHostApplicationLifetime
        {
            private readonly CancellationTokenSource _started = new();
            public CancellationToken ApplicationStarted => _started.Token;
            public CancellationToken ApplicationStopping => CancellationToken.None;
            public CancellationToken ApplicationStopped => CancellationToken.None;
            public void StopApplication() { }
            public void FireStarted() => _started.Cancel();
        }

        [Fact]
        public async Task ApplicationStarted_EmitsStartupAndTableInitMetrics()
        {
            var channel = new CapturingChannel();
            var telemetry = new TelemetryClient(new TelemetryConfiguration { TelemetryChannel = channel, ConnectionString = "InstrumentationKey=00000000-0000-0000-0000-000000000000" });

            // Table storage: unreadable sentinel → full pass → FullPassRan = true.
            var service = new Mock<TableServiceClient>();
            var admin = new Mock<TableClient>();
            service.Setup(s => s.GetTableClient(It.IsAny<string>())).Returns(admin.Object);
            admin.Setup(c => c.GetEntityIfExistsAsync<TableEntity>(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new RequestFailedException(404, "TableNotFound"));
            admin.Setup(c => c.UpsertEntityAsync(It.IsAny<TableEntity>(), It.IsAny<TableUpdateMode>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Mock.Of<Response>());
            service.Setup(s => s.CreateTableIfNotExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Response.FromValue(new TableItem("x"), Mock.Of<Response>()));
            var storage = new TableStorageService(service.Object, NullLogger<TableStorageService>.Instance);
            await storage.InitializeTablesAsync();

            var lifetime = new Lifetime();
            var sut = new StartupTelemetryService(lifetime, telemetry, storage, new BackendBuildInfo(), NullLogger<StartupTelemetryService>.Instance);
            await sut.StartAsync(CancellationToken.None);
            Assert.Empty(channel.Items);

            lifetime.FireStarted();

            var metrics = channel.Items.ConvertAll(i => (MetricTelemetry)i);
            Assert.Equal(2, metrics.Count);
            var startup = metrics.Find(m => m.Name == StartupTelemetryService.StartupMetricName)!;
            var tableInit = metrics.Find(m => m.Name == StartupTelemetryService.TableInitMetricName)!;
            Assert.True(startup.Sum > 0);
            Assert.True(tableInit.Sum >= 0);
            Assert.Equal("True", startup.Properties["tableInitFullPass"]);
            Assert.False(string.IsNullOrEmpty(startup.Properties["version"]));
            Assert.True(startup.Sum >= tableInit.Sum);
        }
    }
}
