using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Builds the <c>types</c> section of shared-manifests.json (schemaVersion 2): a
/// machine-readable description of every HTTP wire type in AutopilotMonitor.Shared —
/// roots are all <see cref="IApiResponse"/> implementers plus every
/// <see cref="WireContractAttribute"/>-marked type, closed transitively over
/// references, collections, dictionaries, enums and <see cref="ProjectedItemsAttribute"/>
/// item types. The web generator (scripts/generate-shared-manifest-types.js) turns it
/// into utils/wire-types.generated.ts.
///
/// Field names honour <see cref="JsonPropertyNameAttribute"/> and fall back to the
/// camelCase policy; <see cref="JsonIgnoreAttribute"/> members are skipped; a nullable
/// slot is exported <c>optional</c> (WhenWritingNull omits its key); get-only members are
/// <c>readonly</c>. Unmappable shapes (non-string dictionary keys, foreign BCL classes)
/// FAIL the build loudly instead of degrading to <c>unknown</c> — an unknown that nobody
/// chose is drift waiting to happen.
/// </summary>
internal static class WireTypeManifestBuilder
{
    // ── public entry ────────────────────────────────────────────────────────

    public static SortedDictionary<string, object?> BuildTypesSection()
    {
        var assembly = typeof(IApiResponse).Assembly;
        var docs = XmlDocs.Load(assembly);

        var roots = assembly.GetTypes()
            .Where(t => !t.IsInterface && !t.IsAbstract &&
                        (typeof(IApiResponse).IsAssignableFrom(t) ||
                         t.GetCustomAttribute<WireContractAttribute>() != null))
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();

        var result = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        var visited = new Dictionary<string, Type>(StringComparer.Ordinal);
        var queue = new Queue<Type>(roots);

        while (queue.Count > 0)
        {
            var type = queue.Dequeue();
            if (visited.TryGetValue(type.Name, out var seen))
            {
                Assert.True(seen == type,
                    $"Wire type simple-name collision: {seen.FullName} vs {type.FullName}. " +
                    "TS interfaces are flat-named — rename one of them.");
                continue;
            }
            visited[type.Name] = type;

            result[type.Name] = type.IsEnum
                ? BuildEnum(type, docs)
                : BuildObject(type, docs, queue);
        }

        return result;
    }

    // ── object / enum shapes ────────────────────────────────────────────────

    private static Dictionary<string, object?> BuildEnum(Type type, XmlDocs docs)
    {
        var node = new Dictionary<string, object?> { ["kind"] = "enum" };
        var doc = docs.Summary("T:" + type.FullName);
        if (doc != null) node["doc"] = doc;
        // Declaration order kept — string-enum unions have no ordinal semantics on the
        // wire (JsonStringEnumConverter), but a stable order keeps diffs minimal.
        node["members"] = Enum.GetNames(type);
        return node;
    }

    private static Dictionary<string, object?> BuildObject(Type type, XmlDocs docs, Queue<Type> queue)
    {
        var node = new Dictionary<string, object?> { ["kind"] = "object" };
        var typeDoc = docs.Summary("T:" + type.FullName);
        if (typeDoc != null) node["doc"] = typeDoc;

        var nullability = new NullabilityInfoContext();
        var fields = new List<object?>();

        // System.Text.Json serializes the DERIVED type's declared properties before the base
        // type's — walk the hierarchy most-derived-first so field order == wire order even for
        // the legacy inherited wire types (LocationSessionRow : SessionSummary). New DTOs stay
        // flat (TypedResponseGuardTests enforces that for every IApiResponse envelope).
        var properties = new List<PropertyInfo>();
        for (var t = type; t != null && t != typeof(object); t = t.BaseType)
        {
            Assert.True(t == type || t.Assembly == typeof(IApiResponse).Assembly,
                $"{type.FullName}: base type {t.FullName} is outside AutopilotMonitor.Shared.");
            properties.AddRange(t
                .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(p => p.GetMethod != null && p.GetMethod.IsPublic && p.GetIndexParameters().Length == 0)
                .Where(p => p.GetCustomAttribute<JsonIgnoreAttribute>() is not { Condition: JsonIgnoreCondition.Always })
                .OrderBy(p => p.MetadataToken)); // declaration order == wire order
        }

        foreach (var p in properties)
        {
            var field = new Dictionary<string, object?>
            {
                ["name"] = p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
                           ?? JsonNamingPolicy.CamelCase.ConvertName(p.Name),
            };

            var projected = p.GetCustomAttribute<ProjectedItemsAttribute>();
            if (projected != null)
            {
                queue.Enqueue(projected.ItemType);
                field["type"] = new Dictionary<string, object?>
                {
                    ["kind"] = "array",
                    ["element"] = new Dictionary<string, object?>
                    {
                        ["kind"] = "partial",
                        ["name"] = projected.ItemType.Name,
                    },
                };
            }
            else
            {
                var info = nullability.Create(p);
                var descriptor = Describe(info.Type, info, queue, $"{type.Name}.{p.Name}");
                // Property-level null never reaches the wire (WhenWritingNull omits the KEY —
                // that's what "optional" says). Explicit nulls only exist INSIDE collections
                // and dictionary values, so the flag stays on nested descriptors only.
                descriptor.Remove("nullable");
                field["type"] = descriptor;
            }

            if (IsOptional(p, nullability)) field["optional"] = true;
            if (p.SetMethod == null || !p.SetMethod.IsPublic) field["readonly"] = true;

            var doc = docs.Summary($"P:{type.FullName}.{p.Name}");
            if (doc != null) field["doc"] = doc;

            fields.Add(field);
        }

        node["fields"] = fields;
        return node;
    }

    /// <summary>A nullable slot's key is ABSENT under WhenWritingNull ⇒ optional in TS.</summary>
    private static bool IsOptional(PropertyInfo p, NullabilityInfoContext ctx)
    {
        if (Nullable.GetUnderlyingType(p.PropertyType) != null) return true;
        if (p.PropertyType.IsValueType) return false;
        return ctx.Create(p).ReadState != NullabilityState.NotNull;
    }

    // ── type descriptors ────────────────────────────────────────────────────

    private static Dictionary<string, object?> Describe(
        Type type, NullabilityInfo? info, Queue<Type> queue, string context)
    {
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying != null)
        {
            var inner = Describe(underlying, null, queue, context);
            inner["nullable"] = true;
            return inner;
        }

        if (Primitive(type) is { } primitive)
        {
            var node = Descriptor("primitive", primitive);
            if (info != null && !type.IsValueType && type != typeof(object) &&
                info.ReadState == NullabilityState.Nullable)
                node["nullable"] = true;
            return node;
        }

        if (type.IsEnum)
        {
            queue.Enqueue(type);
            // JsonStringEnumConverter is part of the wire settings — enums travel as name strings.
            var node = Descriptor("enum", type.Name);
            return node;
        }

        if (type.IsArray)
        {
            var element = Describe(type.GetElementType()!, info?.ElementType, queue, context + "[]");
            return new Dictionary<string, object?> { ["kind"] = "array", ["element"] = element };
        }

        if (type.IsGenericType)
        {
            var args = type.GetGenericTypeArguments(info);
            if (IsDictionaryLike(type))
            {
                Assert.True(args[0].Type == typeof(string),
                    $"{context}: dictionary key must be string for the JSON wire, found {args[0].Type.Name}.");
                var value = Describe(args[1].Type, args[1].Info, queue, context + "{}");
                return new Dictionary<string, object?> { ["kind"] = "map", ["value"] = value };
            }

            if (IsEnumerableLike(type))
            {
                var element = Describe(args[0].Type, args[0].Info, queue, context + "[]");
                return new Dictionary<string, object?> { ["kind"] = "array", ["element"] = element };
            }
        }

        if (type.Assembly == typeof(IApiResponse).Assembly && (type.IsClass || type.IsValueType))
        {
            queue.Enqueue(type);
            var node = Descriptor("ref", type.Name);
            if (info != null && info.ReadState == NullabilityState.Nullable)
                node["nullable"] = true;
            return node;
        }

        throw new InvalidOperationException(
            $"{context}: no wire mapping for {type.FullName}. Map it explicitly in " +
            $"{nameof(WireTypeManifestBuilder)} (or move the type into AutopilotMonitor.Shared) — " +
            "silent 'unknown' fallbacks are drift.");
    }

    private static string? Primitive(Type type)
    {
        if (type == typeof(string) || type == typeof(char)) return "string";
        if (type == typeof(bool)) return "boolean";
        if (type == typeof(int) || type == typeof(long) || type == typeof(short) ||
            type == typeof(byte) || type == typeof(sbyte) || type == typeof(uint) ||
            type == typeof(ulong) || type == typeof(ushort) || type == typeof(double) ||
            type == typeof(float) || type == typeof(decimal)) return "number";
        // Preformatted on the wire by the serializer — TS sees strings.
        if (type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(Guid)) return "string";
        if (type == typeof(object) || type == typeof(JsonElement)) return "unknown";
        return null;
    }

    private static Dictionary<string, object?> Descriptor(string kind, string name)
        => new() { ["kind"] = kind, ["name"] = name };

    private static bool IsDictionaryLike(Type type)
    {
        var def = type.GetGenericTypeDefinition();
        if (def == typeof(Dictionary<,>) || def == typeof(IDictionary<,>) || def == typeof(IReadOnlyDictionary<,>))
            return true;
        return type.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDictionary<,>));
    }

    private static bool IsEnumerableLike(Type type)
        => type.GetInterfaces().Append(type).Any(i =>
            i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));

    private readonly record struct GenericArg(Type Type, NullabilityInfo? Info);

    private static GenericArg[] GetGenericTypeArguments(this Type type, NullabilityInfo? info)
    {
        var args = type.GetGenericArguments();
        var infos = info?.GenericTypeArguments;
        return args.Select((a, i) => new GenericArg(a, infos != null && i < infos.Length ? infos[i] : null))
            .ToArray();
    }

    // ── XML documentation ───────────────────────────────────────────────────

    /// <summary><c>&lt;summary&gt;</c> texts from AutopilotMonitor.Shared.xml, flattened to plain text.</summary>
    private sealed class XmlDocs
    {
        private readonly Dictionary<string, string> _summaries;

        private XmlDocs(Dictionary<string, string> summaries) => _summaries = summaries;

        public static XmlDocs Load(Assembly assembly)
        {
            var path = Path.ChangeExtension(assembly.Location, ".xml");
            Assert.True(File.Exists(path),
                $"XML doc file missing next to {assembly.GetName().Name}.dll — " +
                "GenerateDocumentationFile must stay enabled in AutopilotMonitor.Shared.csproj " +
                "(the wire-type manifest exports <summary> texts as JSDoc).");

            var summaries = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var member in XDocument.Load(path).Descendants("member"))
            {
                var name = member.Attribute("name")?.Value;
                var summary = member.Element("summary");
                if (name == null || summary == null) continue;
                var text = Flatten(summary);
                if (text.Length > 0) summaries[name] = text;
            }
            return new XmlDocs(summaries);
        }

        public string? Summary(string memberId)
            => _summaries.TryGetValue(memberId, out var s) ? s : null;

        private static string Flatten(XElement summary)
        {
            var parts = new List<string>();
            foreach (var node in summary.Nodes())
            {
                switch (node)
                {
                    case XText text:
                        parts.Add(text.Value);
                        break;
                    case XElement { Name.LocalName: "see" or "seealso" } el:
                        var cref = el.Attribute("cref")?.Value;
                        if (!string.IsNullOrEmpty(el.Value)) parts.Add(el.Value);
                        else if (cref != null) parts.Add(cref[(cref.LastIndexOf('.') + 1)..]);
                        else parts.Add(el.Attribute("href")?.Value ?? "");
                        break;
                    case XElement { Name.LocalName: "paramref" or "typeparamref" } el:
                        parts.Add(el.Attribute("name")?.Value ?? "");
                        break;
                    case XElement el:
                        // <c>, <b>, <para>, … — keep the visible text, drop the markup.
                        parts.Add(el.Value);
                        break;
                }
            }
            return Regex.Replace(string.Concat(parts), @"\s+", " ").Trim();
        }
    }
}
