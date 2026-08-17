using System.Text.Json.Nodes;

namespace CW.Server.Transport;

public static class Reply
{
    public static JsonObject Ok()
    {
        return new JsonObject { ["result"] = 0 };
    }

    public static JsonObject Ok(params (string Key, JsonNode? Value)[] fields)
    {
        var payload = Ok();
        foreach (var (key, value) in fields)
        {
            payload[key] = value;
        }

        return payload;
    }

    public static JsonObject Fail(string message, int code = 1)
    {
        return new JsonObject
        {
            ["result"] = code,
            ["error"] = message,
            ["message"] = message,
        };
    }

    public static JsonObject Fail(string message, int code, params (string Key, JsonNode? Value)[] fields)
    {
        var payload = Fail(message, code);
        foreach (var (key, value) in fields)
        {
            payload[key] = value;
        }

        return payload;
    }

    public static JsonObject With(this JsonObject payload, string key, JsonNode? value)
    {
        payload[key] = value;
        return payload;
    }
}
