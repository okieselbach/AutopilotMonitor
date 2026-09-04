using System.IO;
using AutopilotMonitor.Functions.Middleware;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// The catalog and <see cref="PolicyEnforcementMiddleware"/> are the authorization gate for every
/// HTTP route, and the raw table/log proxies rely on the <c>RequestContext</c> it resolves. Nothing
/// at runtime would notice if the registration in <c>Program.cs</c> were dropped or moved in front of
/// <see cref="AuthenticationMiddleware"/> (the policy step reads the principal the auth step stores) —
/// the routes would simply open up. This source scan pins the registration and its order.
/// </summary>
public class MiddlewarePipelineOrderTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AutopilotMonitor.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string ProgramSource() => File.ReadAllText(Path.Combine(
        RepoRoot(), "src", "Backend", "AutopilotMonitor.Functions", "Program.cs"));

    [Fact]
    public void Authentication_then_policy_enforcement_are_registered_in_that_order()
    {
        var src = ProgramSource();

        var auth = src.IndexOf($"UseMiddleware<{nameof(AuthenticationMiddleware)}>()", System.StringComparison.Ordinal);
        var policy = src.IndexOf($"UseMiddleware<{nameof(PolicyEnforcementMiddleware)}>()", System.StringComparison.Ordinal);
        var rateLimit = src.IndexOf($"UseMiddleware<{nameof(UserRateLimitMiddleware)}>()", System.StringComparison.Ordinal);

        Assert.True(auth >= 0, "AuthenticationMiddleware is not registered in Program.cs");
        Assert.True(policy >= 0, "PolicyEnforcementMiddleware is not registered in Program.cs");
        Assert.True(auth < policy, "PolicyEnforcementMiddleware must run AFTER AuthenticationMiddleware (it reads the stored principal)");
        Assert.True(rateLimit < 0 || policy < rateLimit, "UserRateLimitMiddleware keys off the RequestContext the policy step resolves");
    }

    [Fact]
    public void Each_gate_middleware_is_registered_exactly_once()
    {
        var src = ProgramSource();

        Assert.Equal(1, Count(src, $"UseMiddleware<{nameof(AuthenticationMiddleware)}>()"));
        Assert.Equal(1, Count(src, $"UseMiddleware<{nameof(PolicyEnforcementMiddleware)}>()"));
    }

    private static int Count(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, System.StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, System.StringComparison.Ordinal))
            count++;
        return count;
    }
}
