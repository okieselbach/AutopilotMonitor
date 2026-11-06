using System;
using System.Linq;
using System.Text.Json;
using AutopilotMonitor.Functions.Helpers;

namespace AutopilotMonitor.Functions.Security
{
    /// <summary>
    /// Microsoft Entra's answer when it refuses an app token: the HTTP status plus the AADSTS code
    /// from the response body. Only a client error is a rejection; a 5xx, a throttle or a network
    /// failure says nothing about the tenant and never becomes one.
    /// </summary>
    public sealed record EntraTokenRejection(int StatusCode, string? ErrorCode, string? Description)
    {
        /// <summary>The app has no service principal in the tenant: its enterprise application was removed.</summary>
        public const string ServicePrincipalMissingCode = "AADSTS7000229";

        /// <summary>
        /// Not a refusal in Entra's words but the same lockout: a token without any application role,
        /// which Graph refuses on every call. Typically a removed enterprise application that a user's
        /// sign-in consent re-created. The repair is the same admin consent.
        /// </summary>
        public static readonly EntraTokenRejection NoApplicationRole =
            new(200, "NoApplicationRole", "The app token carries no application permission");

        private const string CodePrefix = "AADSTS";
        private const int MaxDescriptionLength = 300;

        // Wrong, expired or missing client secret: our configuration, never the customer's tenant.
        private static readonly string[] OurCredentialCodes = { "AADSTS7000215", "AADSTS7000222", "AADSTS7000218" };

        public bool IsServicePrincipalMissing =>
            string.Equals(ErrorCode, ServicePrincipalMissingCode, StringComparison.OrdinalIgnoreCase);

        public bool IsOurCredential =>
            ErrorCode != null && OurCredentialCodes.Contains(ErrorCode, StringComparer.OrdinalIgnoreCase);

        /// <summary>Reads the code and the description from a token error body; a body that is not JSON keeps only the status.</summary>
        public static EntraTokenRejection Parse(int statusCode, string? body)
        {
            var json = TryParseObject(body);
            var description = json is { } root ? UpstreamJson.GetString(root, "error_description") : null;
            return new EntraTokenRejection(statusCode, ReadCode(json, description), Shorten(description));
        }

        private static JsonElement? TryParseObject(string? body)
        {
            try
            {
                return UpstreamJson.ParseObjectOrNull(body);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string? ReadCode(JsonElement? json, string? description)
        {
            if (json is { } root)
            {
                // The first whole number that fits Int64; text, fractions and larger numbers are skipped.
                foreach (var code in UpstreamJson.Items(root, "error_codes"))
                {
                    if (code.ValueKind == JsonValueKind.Number && code.TryGetInt64(out var value))
                        return CodePrefix + value;
                }
            }

            // Fallback for a body without error_codes: the description starts with the code.
            var start = description?.IndexOf(CodePrefix, StringComparison.Ordinal) ?? -1;
            if (start < 0) return null;
            var digits = new string(description!.Skip(start + CodePrefix.Length).TakeWhile(char.IsDigit).ToArray());
            return digits.Length == 0 ? null : CodePrefix + digits;
        }

        private static string? Shorten(string? description)
        {
            if (string.IsNullOrWhiteSpace(description)) return null;
            var line = description.Split('\n')[0].Trim();
            return line.Length <= MaxDescriptionLength ? line : line.Substring(0, MaxDescriptionLength);
        }
    }
}
