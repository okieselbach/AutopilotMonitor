using System.Text.Json;
using AutopilotMonitor.Functions.Helpers;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Serializes test payloads with the PRODUCTION wire options (<see cref="ApiJsonOptions"/> —
/// camelCase, WhenWritingNull), the policy every test harness with its own serializer must
/// follow. Shape tests serialized DTOs with default options before the 2026-08-31 completion
/// pass; that only worked while the payloads were anonymous objects with lowercase members.
/// Generic so the CONCRETE type reaches System.Text.Json (a declared object parameter would
/// too, but the generic also keeps compile-time typing at the call site).
/// </summary>
internal static class TestWire
{
    private static readonly JsonSerializerOptions Options = ApiJsonOptions.Create();

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static JsonElement SerializeToElement<T>(T value) => JsonSerializer.SerializeToElement(value, Options);
}
