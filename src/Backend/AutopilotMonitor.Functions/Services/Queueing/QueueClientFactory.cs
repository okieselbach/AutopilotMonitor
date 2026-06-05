using System;
using Azure.Identity;
using Azure.Storage.Queues;
using Microsoft.Extensions.Configuration;

namespace AutopilotMonitor.Functions.Services.Queueing
{
    /// <summary>
    /// Single source of truth for building <see cref="QueueClient"/> instances against the
    /// project's storage account. Every queue producer / worker / probe used to hand-roll the
    /// same <c>AzureStorageAccountName</c> (Managed Identity) vs
    /// <c>AzureTableStorageConnectionString</c> (fallback) branch — ~25 duplicated lines per
    /// site across 13 sites (code-quality audit 2026-05-29, finding D2). This factory resolves
    /// the credential model <b>once</b> in its constructor and hands out clients on demand.
    /// <para>
    /// Registered as a singleton; the resolved credential / connection string are captured in
    /// closures so each <see cref="Create(string, bool)"/> call is allocation-cheap and never
    /// re-reads configuration.
    /// </para>
    /// </summary>
    public sealed class QueueClientFactory
    {
        private const string PoisonQueueSuffix = "-poison";

        private readonly Func<string, QueueClientOptions, QueueClient> _builder;

        public QueueClientFactory(IConfiguration configuration)
        {
            if (configuration is null) throw new ArgumentNullException(nameof(configuration));

            var storageAccountName = configuration["AzureStorageAccountName"];
            var connectionString = configuration["AzureTableStorageConnectionString"];

            if (!string.IsNullOrEmpty(storageAccountName))
            {
                // Managed Identity (production). Resolve the credential once — DefaultAzureCredential
                // caches tokens internally and is safe to share across all queue clients.
                var credential = new DefaultAzureCredential();
                _builder = (queueName, options) =>
                {
                    var uri = new Uri(
                        $"https://{storageAccountName}.queue.core.windows.net/{queueName}");
                    return new QueueClient(uri, credential, options);
                };
            }
            else if (!string.IsNullOrEmpty(connectionString))
            {
                _builder = (queueName, options) => new QueueClient(connectionString, queueName, options);
            }
            else
            {
                throw new InvalidOperationException(
                    "Queue Storage not configured. Set either 'AzureStorageAccountName' (for Managed Identity) or 'AzureTableStorageConnectionString'.");
            }
        }

        /// <summary>
        /// Builds a <see cref="QueueClient"/> for <paramref name="queueName"/>.
        /// </summary>
        /// <param name="base64">
        /// When <c>true</c> (default) the client encodes message bodies as Base64 — required for
        /// round-trip compatibility with the Azure Functions QueueTrigger default and with the
        /// producer/worker pairs that consume these queues. Pass <c>false</c> for read-only probes
        /// (e.g. <c>GetPropertiesAsync</c> message-count checks) where encoding is irrelevant.
        /// </param>
        public QueueClient Create(string queueName, bool base64 = true)
        {
            if (string.IsNullOrWhiteSpace(queueName))
                throw new ArgumentException("Queue name must not be empty.", nameof(queueName));

            var options = base64
                ? new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 }
                : new QueueClientOptions();
            return _builder(queueName, options);
        }

        /// <summary>
        /// Builds the main + poison <see cref="QueueClient"/> pair for a worker. When
        /// <paramref name="poisonQueueName"/> is null the poison queue defaults to
        /// <paramref name="mainQueueName"/> + <c>"-poison"</c> (the convention every worker
        /// previously hard-coded inline).
        /// </summary>
        public (QueueClient Main, QueueClient Poison) CreatePair(
            string mainQueueName, string? poisonQueueName = null, bool base64 = true)
        {
            var main = Create(mainQueueName, base64);
            var poison = Create(poisonQueueName ?? mainQueueName + PoisonQueueSuffix, base64);
            return (main, poison);
        }
    }
}
