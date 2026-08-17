using CW.Server.Infrastructure;
using Microsoft.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace CW.Server.Transport;

public interface ILegacyResponseWriter
{
    Task WriteAsync(HttpContext context, JsonNode? payload, CancellationToken cancellationToken);

    Task WriteRawAsync(HttpContext context, byte[] payload, CancellationToken cancellationToken);
}

public sealed class LegacyResponseWriter : ILegacyResponseWriter
{
    public Task WriteAsync(HttpContext context, JsonNode? payload, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(Json.Write(payload));
        return WriteRawAsync(context, bytes, cancellationToken);
    }

    public async Task WriteRawAsync(HttpContext context, byte[] payload, CancellationToken cancellationToken)
    {
        var response = context.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "application/json";
        response.ContentLength = payload.Length;
        response.Headers[HeaderNames.Connection] = "close";

        if (HttpMethods.IsHead(context.Request.Method))
        {
            await response.Body.FlushAsync(cancellationToken);
            return;
        }

        await response.Body.WriteAsync(payload, cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }
}
