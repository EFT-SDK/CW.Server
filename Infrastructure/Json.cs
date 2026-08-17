using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CW.Server.Infrastructure;

public static class Json
{
    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = false };

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    public static JsonNode? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(text, NodeOptions, DocumentOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string Write(JsonNode? node)
    {
        return node?.ToJsonString(SerializerOptions) ?? "null";
    }

    public static JsonObject Object() => new();

    public static JsonNode? Clone(JsonNode? node)
    {
        return node?.DeepClone();
    }

    public static JsonObject CloneObject(JsonNode? node)
    {
        return node?.DeepClone() as JsonObject ?? new JsonObject();
    }

    public static JsonArray CloneArray(JsonNode? node)
    {
        return node?.DeepClone() as JsonArray ?? new JsonArray();
    }

    public static JsonObject Obj(this JsonNode? node, string key)
    {
        if (node is JsonObject o && o.TryGetPropertyValue(key, out var child) && child is JsonObject result)
        {
            return result;
        }

        return new JsonObject();
    }

    public static JsonObject EnsureObject(this JsonObject parent, string key)
    {
        if (parent.TryGetPropertyValue(key, out var existing) && existing is JsonObject found)
        {
            return found;
        }

        var created = new JsonObject();
        parent[key] = created;
        return created;
    }

    public static JsonArray EnsureArray(this JsonObject parent, string key)
    {
        if (parent.TryGetPropertyValue(key, out var existing) && existing is JsonArray found)
        {
            return found;
        }

        var created = new JsonArray();
        parent[key] = created;
        return created;
    }

    public static JsonArray Arr(this JsonNode? node, string key)
    {
        if (node is JsonObject o && o.TryGetPropertyValue(key, out var child) && child is JsonArray result)
        {
            return result;
        }

        return new JsonArray();
    }

    public static JsonNode? Get(this JsonNode? node, string key)
    {
        return node is JsonObject o && o.TryGetPropertyValue(key, out var child) ? child : null;
    }

    public static bool Has(this JsonNode? node, string key)
    {
        return node is JsonObject o && o.ContainsKey(key);
    }

    public static int ToInt(JsonNode? node, int fallback = 0)
    {
        if (node is null)
        {
            return fallback;
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue<int>(out var i))
            {
                return i;
            }

            if (value.TryGetValue<long>(out var l))
            {
                return unchecked((int)l);
            }

            if (value.TryGetValue<double>(out var d))
            {
                return double.IsFinite(d) ? (int)d : fallback;
            }

            if (value.TryGetValue<bool>(out var b))
            {
                return b ? 1 : 0;
            }

            if (value.TryGetValue<string>(out var s))
            {
                return ParseInt(s, fallback);
            }
        }

        return fallback;
    }

    public static long ToLong(JsonNode? node, long fallback = 0)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<long>(out var l))
            {
                return l;
            }

            if (value.TryGetValue<double>(out var d))
            {
                return double.IsFinite(d) ? (long)d : fallback;
            }

            if (value.TryGetValue<string>(out var s) &&
                long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }

        return ToInt(node, (int)fallback);
    }

    public static double ToDouble(JsonNode? node, double fallback = 0)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<double>(out var d))
            {
                return d;
            }

            if (value.TryGetValue<string>(out var s) &&
                double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }

        return ToInt(node, (int)fallback);
    }

    public static bool ToBool(JsonNode? node, bool fallback = false)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<bool>(out var b))
            {
                return b;
            }

            if (value.TryGetValue<string>(out var s))
            {
                return bool.TryParse(s, out var parsed) ? parsed : ParseInt(s, 0) != 0;
            }
        }

        return ToInt(node, fallback ? 1 : 0) != 0;
    }

    public static string ToText(JsonNode? node, string fallback = "")
    {
        if (node is null)
        {
            return fallback;
        }

        if (node is JsonValue value && value.TryGetValue<string>(out var s))
        {
            return s;
        }

        return node.ToJsonString(SerializerOptions).Trim('"');
    }

    public static int ParseInt(string? text, int fallback = 0)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var asDouble)
            && double.IsFinite(asDouble))
        {
            return (int)asDouble;
        }

        return fallback;
    }

    public static bool IsIntegerText(string? text)
    {
        return !string.IsNullOrEmpty(text)
            && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
    }
}
