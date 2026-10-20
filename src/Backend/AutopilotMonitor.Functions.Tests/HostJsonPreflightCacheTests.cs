using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Browser preflights never reach the worker: on Flex Consumption the Functions host answers every CORS
/// preflight itself, and its policy carries no max-age. host.json's http <c>customHeaders</c> are stamped
/// by the host on every response, the short-circuited preflight included, so they are the only place
/// <c>Access-Control-Max-Age</c> can come from. Dropping the entry silently costs every repeated
/// cross-origin API call a preflight round trip again; an <c>Access-Control-Allow-*</c> entry there would
/// be stamped on responses to origins the host's CORS policy rejected.
/// </summary>
public class HostJsonPreflightCacheTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AutopilotMonitor.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static JsonElement CustomHeaders()
    {
        var path = Path.Combine(RepoRoot(), "src", "Backend", "AutopilotMonitor.Functions", "host.json");
        var options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        using var doc = JsonDocument.Parse(File.ReadAllText(path), options);
        return doc.RootElement.GetProperty("extensions").GetProperty("http").GetProperty("customHeaders").Clone();
    }

    [Fact]
    public void CustomHeaders_CarryThePreflightMaxAge()
    {
        Assert.Equal("7200", CustomHeaders().GetProperty("Access-Control-Max-Age").GetString());
    }

    [Fact]
    public void CustomHeaders_HoldNothingButTheMaxAge()
    {
        var names = CustomHeaders().EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(new[] { "Access-Control-Max-Age" }, names);
    }
}
