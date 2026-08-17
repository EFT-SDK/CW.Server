using System.IO.Compression;
using System.Text;

namespace CW.Server.Infrastructure;

public static class ZlibCodec
{
    public static string DecodeText(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty)
        {
            return string.Empty;
        }

        var buffer = payload.ToArray();

        if (TryInflate(buffer, CompressionKind.Zlib, out var zlib))
        {
            return zlib;
        }

        if (TryInflate(buffer, CompressionKind.Deflate, out var deflate))
        {
            return deflate;
        }

        if (TryInflate(buffer, CompressionKind.GZip, out var gzip))
        {
            return gzip;
        }

        return Encoding.UTF8.GetString(buffer);
    }

    private static bool TryInflate(byte[] payload, CompressionKind kind, out string text)
    {
        try
        {
            using var source = new MemoryStream(payload, writable: false);
            using Stream decompressor = kind switch
            {
                CompressionKind.Zlib => new ZLibStream(source, CompressionMode.Decompress),
                CompressionKind.Deflate => new DeflateStream(source, CompressionMode.Decompress),
                _ => new GZipStream(source, CompressionMode.Decompress),
            };

            using var target = new MemoryStream();
            decompressor.CopyTo(target);
            text = Encoding.UTF8.GetString(target.ToArray());
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException)
        {
            text = string.Empty;
            return false;
        }
    }

    private enum CompressionKind
    {
        Zlib,
        Deflate,
        GZip,
    }
}
