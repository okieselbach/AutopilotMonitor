using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace AutopilotMonitor.Functions.Middleware
{
    // Registers ASP.NET Core response-compression and request-decompression middleware into the
    // Kestrel pipeline that the Azure Functions isolated worker hosts via
    // Microsoft.Azure.Functions.Worker.Extensions.Http.AspNetCore. Using the standard middleware
    // keeps gzip handling out of function code.
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
