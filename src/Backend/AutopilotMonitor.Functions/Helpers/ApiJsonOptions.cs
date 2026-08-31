using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutopilotMonitor.Functions.Helpers;

/// <summary>
/// The single source of the production JSON wire settings (camelCase keys, absent-when-null,
/// string enums). Program.cs applies them to the worker serializer; test harnesses that build
/// their own <c>JsonObjectSerializer</c> MUST use <see cref="Create"/> so assertions run against
/// the real wire shape — a bare <c>new JsonObjectSerializer()</c> serializes PascalCase and
/// writes nulls, which lets a test pass while the deployed wire differs.
/// </summary>
public static class ApiJsonOptions
{
    /// <summary>Apply the wire settings to an existing options instance (Program.cs).</summary>
    public static void Apply(JsonSerializerOptions options)
    {
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        options.Converters.Add(new JsonStringEnumConverter());
    }

    /// <summary>Fresh options carrying exactly the production wire settings.</summary>
    public static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions();
        Apply(options);
        return options;
    }
}
