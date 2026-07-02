using System.Text.Json;
using System.Text.Json.Nodes;

namespace SmtpGateway.Admin.Tui;

/// <summary>
/// Generic dotted-path (":"-delimited, matching Microsoft.Extensions.Configuration's own key
/// delimiter) read/write access to the raw appsettings.json document via
/// <see cref="System.Text.Json.Nodes.JsonNode"/>, so 'config show'/'config set' can view and edit
/// every documented <see cref="SmtpGateway.Infrastructure.GatewayOptions"/> value - including
/// provider secrets, shown/written in cleartext by design - without a bespoke command per field.
/// Unrelated keys are preserved because only the addressed node is ever touched.
/// </summary>
public static class ConfigDocument
{
    public const string GatewaySectionName = "Gateway";

    private static readonly JsonNodeOptions NodeOptions = new();
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>
    /// Parses the whole appsettings.json file into a mutable <see cref="JsonObject"/>. Comments
    /// are tolerated on read but are not preserved on <see cref="Save"/> - there is no
    /// backup/rollback/formatting-preservation guarantee, matching the "no hot reload, no
    /// history" product decision for config edits.
    /// </summary>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="InvalidOperationException">The file is not a JSON object at its root.</exception>
    public static JsonObject LoadRoot(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Configuration file '{path}' was not found.", path);
        }

        var text = File.ReadAllText(path);
        var node = JsonNode.Parse(text, NodeOptions, DocumentOptions);
        return node as JsonObject
            ?? throw new InvalidOperationException($"'{path}' does not contain a JSON object at its root.");
    }

    public static void Save(JsonObject root, string path) =>
        File.WriteAllText(path, root.ToJsonString(WriteOptions));

    /// <summary>Gets the existing "Gateway" section, or creates and attaches an empty one if missing.</summary>
    /// <exception cref="InvalidOperationException">"Gateway" exists but is not a JSON object.</exception>
    public static JsonObject GetOrCreateGatewaySection(JsonObject root)
    {
        ArgumentNullException.ThrowIfNull(root);

        if (root[GatewaySectionName] is JsonObject gateway)
        {
            return gateway;
        }

        if (root[GatewaySectionName] is not null)
        {
            throw new InvalidOperationException($"'{GatewaySectionName}' exists in the config file but is not an object.");
        }

        var created = new JsonObject();
        root[GatewaySectionName] = created;
        return created;
    }

    /// <summary>Flattens every leaf value under <paramref name="section"/> into dotted paths, sorted for stable display.</summary>
    public static IReadOnlyList<(string Path, string Value)> Flatten(JsonObject section)
    {
        ArgumentNullException.ThrowIfNull(section);

        var results = new List<(string Path, string Value)>();
        FlattenInto(section, string.Empty, results);
        results.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return results;
    }

    /// <summary>
    /// Reads a single dotted path's leaf value, or null if the path does not resolve to a present
    /// scalar leaf. A JSON null leaf (and an absent key) both yield C# null - "no value" - rather
    /// than the literal string "null": callers such as the setup wizard's Prefill treat any non-null
    /// return as a real configured value, so returning "null" here would, for example, accidentally
    /// enable inbound AUTH from a shipped <c>"AuthUsername": null</c>. (JSON null and an absent key
    /// are indistinguishable at the <see cref="JsonObject"/> indexer level - both surface as C# null
    /// - which is fine: neither is a configured value. Note <see cref="Flatten"/> still renders a
    /// null leaf as "null" for display because it formats leaves directly, not via this method.)
    /// </summary>
    public static string? GetPath(JsonObject section, string dottedPath)
    {
        ArgumentNullException.ThrowIfNull(section);

        JsonNode? current = section;
        foreach (var segment in SplitPath(dottedPath))
        {
            if (current is not JsonObject obj)
            {
                return null;
            }

            current = obj[segment];
        }

        return current is null or JsonObject or JsonArray ? null : FormatLeaf(current);
    }

    /// <summary>
    /// Sets a single leaf value addressed by <paramref name="dottedPath"/>, creating intermediate
    /// objects for path segments that do not yet exist. The value is always written as a JSON
    /// string: Microsoft.Extensions.Configuration flattens every JSON scalar (number, bool,
    /// string) down to a plain string before the options binder converts it to the target
    /// property's type, so a JSON string round-trips identically to a native JSON number/bool for
    /// binding purposes - guessing the "real" type here would only add risk (e.g. misclassifying a
    /// numeric-looking secret) for no behavioral benefit.
    /// </summary>
    /// <exception cref="FormatException">The path is empty, has an empty segment, or runs through an existing non-object value.</exception>
    public static void SetPath(JsonObject section, string dottedPath, string value)
    {
        ArgumentNullException.ThrowIfNull(section);
        ArgumentNullException.ThrowIfNull(value);

        var segments = SplitPath(dottedPath);

        var current = section;
        for (var i = 0; i < segments.Count - 1; i++)
        {
            var segment = segments[i];
            var child = current[segment];
            switch (child)
            {
                case JsonObject childObject:
                    current = childObject;
                    break;
                case null:
                    var created = new JsonObject();
                    current[segment] = created;
                    current = created;
                    break;
                default:
                    throw new FormatException(
                        $"Cannot set '{dottedPath}': '{segment}' already holds a non-object value.");
            }
        }

        current[segments[^1]] = JsonValue.Create(value);
    }

    private static void FlattenInto(JsonNode? node, string prefix, List<(string Path, string Value)> results)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    FlattenInto(value, prefix.Length == 0 ? key : $"{prefix}:{key}", results);
                }

                break;

            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    FlattenInto(array[i], $"{prefix}:{i}", results);
                }

                break;

            default:
                results.Add((prefix, FormatLeaf(node)));
                break;
        }
    }

    private static string FormatLeaf(JsonNode? node)
    {
        if (node is null)
        {
            return "null";
        }

        if (node is JsonValue value && value.TryGetValue<string>(out var s))
        {
            return s;
        }

        return node.ToJsonString();
    }

    private static IReadOnlyList<string> SplitPath(string dottedPath)
    {
        if (string.IsNullOrWhiteSpace(dottedPath))
        {
            throw new FormatException("Path must not be empty.");
        }

        var segments = dottedPath.Split(':');
        if (segments.Any(string.IsNullOrWhiteSpace))
        {
            throw new FormatException($"'{dottedPath}' is not a valid dotted path (empty segment).");
        }

        return segments;
    }
}
