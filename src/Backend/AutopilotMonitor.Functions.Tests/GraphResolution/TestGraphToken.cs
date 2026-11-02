using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace AutopilotMonitor.Functions.Tests.GraphResolution;

/// <summary>What Entra's token endpoint answers on success, with the application roles of the tenant's service principal.</summary>
internal static class TestGraphToken
{
    public const string ValidationRole = "DeviceManagementServiceConfig.Read.All";

    public static string Body(params string[] roles)
    {
        var jwt = new JwtSecurityToken(
            issuer: "https://sts.windows.net/test",
            audience: "https://graph.microsoft.com",
            claims: roles.Select(r => new Claim("roles", r)),
            notBefore: null,
            expires: DateTime.UtcNow.AddHours(1));
        return $"{{\"access_token\":\"{new JwtSecurityTokenHandler().WriteToken(jwt)}\",\"expires_in\":3599}}";
    }
}
