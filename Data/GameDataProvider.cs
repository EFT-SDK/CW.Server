using CW.Server.Configuration;
using CW.Server.Infrastructure;
using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace CW.Server.Data;

public interface IGameDataProvider
{
    JsonObject Backend(string name);

    byte[]? BackendRaw(string name);

    bool BackendExists(string name);

    JsonObject Template(string name);

    JsonObject TemplateCopy(string name);

    int BackendDatasetCount { get; }
}

public sealed class GameDataProvider : IGameDataProvider
{
    private readonly ServerPaths _paths;
    private readonly IJsonFileStore _files;
    private readonly ConcurrentDictionary<string, JsonObject> _backend = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, JsonObject> _templates = new(StringComparer.OrdinalIgnoreCase);

    public GameDataProvider(ServerPaths paths, IJsonFileStore files)
    {
        _paths = paths;
        _files = files;
    }

    public int BackendDatasetCount =>
        Directory.Exists(_paths.BackendData)
            ? Directory.GetFiles(_paths.BackendData, "*.json").Length
            : 0;

    public JsonObject Backend(string name)
    {
        return _backend.GetOrAdd(name, key => _files.LoadObject(_paths.BackendFile(Normalize(key))));
    }

    public byte[]? BackendRaw(string name)
    {
        return _files.ReadRaw(_paths.BackendFile(Normalize(name)));
    }

    public bool BackendExists(string name)
    {
        return _files.Exists(_paths.BackendFile(Normalize(name)));
    }

    public JsonObject Template(string name)
    {
        return _templates.GetOrAdd(name, key => _files.LoadObject(_paths.TemplateFile(Normalize(key))));
    }

    public JsonObject TemplateCopy(string name)
    {
        return Json.CloneObject(Template(name));
    }

    private static string Normalize(string name)
    {
        return name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? name : name + ".json";
    }
}
