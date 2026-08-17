using CW.Server.Infrastructure;
using System.Text.Json.Nodes;

namespace CW.Server.Transport;

public sealed class LegacyRequest
{
    public required string Method { get; init; }

    public required string Path { get; init; }

    public required string RawQuery { get; init; }

    public required IReadOnlyDictionary<string, string> Query { get; init; }

    public required string RawBody { get; init; }

    public JsonNode? Body { get; init; }

    public required string ClientIp { get; init; }

    public JsonObject BodyObject => Body as JsonObject ?? new JsonObject();

    public string? Value(string key) => Query.TryGetValue(key, out var value) ? value : null;

    public string Text(string key, string fallback = "") => Value(key) ?? fallback;

    public int Int(string key, int fallback = 0) => Json.ParseInt(Value(key), fallback);

    public bool Bool(string key, bool fallback = false)
    {
        var raw = Value(key);
        if (string.IsNullOrEmpty(raw))
        {
            return fallback;
        }

        return raw is "1" or "true" or "True" or "yes";
    }

    public bool Contains(string key) => Query.ContainsKey(key);

    public string Action => (Value("action") ?? string.Empty).ToLowerInvariant();

    public string AdmQuery => Value("q") ?? string.Empty;
}
