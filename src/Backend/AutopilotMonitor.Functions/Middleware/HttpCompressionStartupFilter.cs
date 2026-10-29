using System.IO.Compression;
using AutopilotMonitor.Functions.Helpers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;

namespace AutopilotMonitor.Functions.Middleware
{
    /// <summary>
    /// The worker's HTTP pipeline beyond the Functions defaults: gzip in both directions and the
    /// request body limit. Program.cs and the pipeline tests both register it through
    /// <see cref="AddWorkerHttpPipeline"/>, so the tests exercise the production registration.
    /// </summary>
    internal static class WorkerHttpPipelineRegistration
    {
        public static IServiceCollection AddWorkerHttpPipeline(this IServiceCollection services)
        {
            // UseResponseCompression: backend → agent (config, ingest ack, ...). Triggered by Accept-Encoding.
            // UseRequestDecompression: agent → backend (telemetry ingest). Triggered by Content-Encoding.
            services.AddResponseCompression(options =>
            {
                options.EnableForHttps = true;
                options.Providers.Add<GzipCompressionProvider>();
                options.MimeTypes = ResponseCompressionDefaults.MimeTypes
                    .Concat(new[] { "application/json", "application/x-ndjson" });
            });
            services.Configure<GzipCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);
            services.AddRequestDecompression();
            services.AddTransient<IStartupFilter, HttpCompressionStartupFilter>();

            // Pinned rather than left at Kestrel's default, because UseRequestDecompression applies the
            // same limit to the inflated body. PostConfigure, so that neither registration order nor a
            // Kestrel:Limits app setting can lift it.
            services.PostConfigure<KestrelServerOptions>(o => o.Limits.MaxRequestBodySize = RequestBody.WorkerMaxBytes);
            return services;
        }
    }

    // Registers ASP.NET Core response-compression and request-decompression middleware into the
    // Kestrel pipeline that the Azure Functions isolated worker hosts via
    // Microsoft.Azure.Functions.Worker.Extensions.Http.AspNetCore. Using the standard middleware
    // keeps gzip handling out of function code. A startup filter, because
    // FunctionsApplication.CreateBuilder's Build() returns IHost, not WebApplication — there is no
    // app.UseXxx() to call.
    internal sealed class HttpCompressionStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return builder =>
            {
                builder.UseResponseCompression();
                builder.UseRequestDecompression();
                next(builder);
            };
        }
    }
}
