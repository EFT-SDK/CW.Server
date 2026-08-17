using CW.Server.Infrastructure;
using Microsoft.AspNetCore.WebUtilities;
using System.Text.Json.Nodes;

namespace CW.Server.Transport;

public interface ILegacyRequestReader
{
    Task<LegacyRequest> ReadAsync(HttpContext context, CancellationToken cancellationToken);
}

public sealed class LegacyRequestReader : ILegacyRequestReader
{
    public async Task<LegacyRequest> ReadAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var request = context.Request;
        var rawQuery = request.QueryString.HasValue
            ? request.QueryString.Value!.TrimStart('?')
            : string.Empty;

        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in QueryHelpers.ParseQuery(request.QueryString.Value ?? string.Empty))
        {
            var value = pair.Value.Count > 0 ? pair.Value[0] : null;
            query[pair.Key] = value ?? string.Empty;
        }

        var rawBody = await ReadBodyAsync(request, cancellationToken);
        var parsed = Json.Parse(rawBody);
        JsonNode? body = parsed;

        if (body is null && !string.IsNullOrEmpty(rawBody))
        {
            body = JsonValue.Create(rawBody);
        }

        return new LegacyRequest
        {
            Method = request.Method,
            Path = request.Path.HasValue ? request.Path.Value! : "/",
            RawQuery = rawQuery,
            Query = query,
            RawBody = rawBody,
            Body = body,
            ClientIp = context.Connection.RemoteIpAddress?.ToString() ?? "0.0.0.0",
        };
    }

    private static async Task<string> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentLength is 0)
        {
            return string.Empty;
        }

        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, cancellationToken);

        if (buffer.Length == 0)
        {
            return string.Empty;
        }

        var text = ZlibCodec.DecodeText(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
        return text.Trim('\0').Trim();
    }
}
