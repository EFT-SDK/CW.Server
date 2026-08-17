using System.Text;
using System.Text.Json.Nodes;

namespace CW.Server.Infrastructure;

public interface IJsonFileStore
{
    JsonNode? Load(string path);

    JsonObject LoadObject(string path);

    JsonArray LoadArray(string path);

    void Save(string path, JsonNode? node);

    bool Exists(string path);

    byte[]? ReadRaw(string path);
}

public sealed class JsonFileStore : IJsonFileStore
{
    private const int MaxAttempts = 5;

    private readonly ILogger<JsonFileStore> _logger;

    public JsonFileStore(ILogger<JsonFileStore> logger)
    {
        _logger = logger;
    }

    public bool Exists(string path) => File.Exists(path);

    public byte[]? ReadRaw(string path)
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            try
            {
                return File.Exists(path) ? File.ReadAllBytes(path) : null;
            }
            catch (IOException)
            {
                Thread.Sleep(5 * (attempt + 1));
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(5 * (attempt + 1));
            }
        }

        _logger.LogWarning("unable to read {Path}", path);
        return null;
    }

    public JsonNode? Load(string path)
    {
        var raw = ReadRaw(path);
        if (raw is null || raw.Length == 0)
        {
            return null;
        }

        var text = Encoding.UTF8.GetString(raw).TrimStart('﻿');
        return Json.Parse(text);
    }

    public JsonObject LoadObject(string path) => Load(path) as JsonObject ?? new JsonObject();

    public JsonArray LoadArray(string path) => Load(path) as JsonArray ?? new JsonArray();

    public void Save(string path, JsonNode? node)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var payload = Encoding.UTF8.GetBytes(Json.Write(node));
        var temp = $"{path}.{Environment.CurrentManagedThreadId}.tmp";

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            try
            {
                File.WriteAllBytes(temp, payload);
                File.Move(temp, path, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < MaxAttempts - 1)
            {
                Thread.Sleep(10 * (attempt + 1));
            }
            catch (UnauthorizedAccessException) when (attempt < MaxAttempts - 1)
            {
                Thread.Sleep(10 * (attempt + 1));
            }
        }

        _logger.LogError("unable to persist {Path}", path);
        TryDelete(temp);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
