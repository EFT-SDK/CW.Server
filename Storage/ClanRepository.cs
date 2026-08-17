using CW.Server.Configuration;
using CW.Server.Infrastructure;
using System.Text.Json.Nodes;

namespace CW.Server.Storage;

public interface IClanRepository
{
    JsonObject All();

    JsonObject? ById(string clanId);

    (string? ClanId, JsonObject? Clan) ClanOf(int userId);

    T Mutate<T>(Func<JsonObject, T> mutate);

    void Mutate(Action<JsonObject> mutate);

    int NextClanId();
}

public sealed class ClanRepository : IClanRepository
{
    private const int FirstClanId = 900001;

    private readonly ServerPaths _paths;
    private readonly IJsonFileStore _files;
    private readonly StateLock _lock;

    public ClanRepository(ServerPaths paths, IJsonFileStore files, StateLock stateLock)
    {
        _paths = paths;
        _files = files;
        _lock = stateLock;
    }

    public JsonObject All()
    {
        using (_lock.Enter())
        {
            return _files.LoadObject(Path());
        }
    }

    public JsonObject? ById(string clanId)
    {
        return All()[clanId] as JsonObject;
    }

    public (string? ClanId, JsonObject? Clan) ClanOf(int userId)
    {
        foreach (var pair in All())
        {
            if (pair.Value is not JsonObject clan)
            {
                continue;
            }

            if (clan.Arr("members").Any(m => Json.ToInt(m) == userId))
            {
                return (pair.Key, clan);
            }
        }

        return (null, null);
    }

    public T Mutate<T>(Func<JsonObject, T> mutate)
    {
        using (_lock.Enter())
        {
            var data = _files.LoadObject(Path());
            var result = mutate(data);
            _files.Save(Path(), data);
            return result;
        }
    }

    public void Mutate(Action<JsonObject> mutate)
    {
        Mutate<object?>(data =>
        {
            mutate(data);
            return null;
        });
    }

    public int NextClanId()
    {
        var ids = All()
            .Select(p => p.Key)
            .Where(Json.IsIntegerText)
            .Select(k => Json.ParseInt(k))
            .ToList();

        return ids.Count > 0 ? ids.Max() + 1 : FirstClanId;
    }

    private string Path() => _paths.StateFile("clans.json");
}
