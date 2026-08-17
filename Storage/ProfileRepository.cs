using CW.Server.Configuration;
using CW.Server.Infrastructure;
using CW.Server.Services;
using Microsoft.Extensions.Options;
using System.Text.Json.Nodes;

namespace CW.Server.Storage;

public interface IProfileRepository
{
    JsonObject Get(int userId, string? nick = null);

    void Save(int userId, JsonObject profile);

    JsonObject MutateData(int userId, Action<JsonObject> mutate);
}

public sealed class ProfileRepository : IProfileRepository
{
    private readonly ServerPaths _paths;
    private readonly IJsonFileStore _files;
    private readonly StateLock _lock;
    private readonly ProfileFactory _factory;
    private readonly ProfileNormalizer _normalizer;
    private readonly ServerOptions _options;

    public ProfileRepository(
        ServerPaths paths,
        IJsonFileStore files,
        StateLock stateLock,
        ProfileFactory factory,
        ProfileNormalizer normalizer,
        IOptions<ServerOptions> options)
    {
        _paths = paths;
        _files = files;
        _lock = stateLock;
        _factory = factory;
        _normalizer = normalizer;
        _options = options.Value;
    }

    public JsonObject Get(int userId, string? nick = null)
    {
        using (_lock.Enter())
        {
            var path = PathFor(userId);
            var stored = _files.Load(path) as JsonObject;

            if (stored is null)
            {
                var name = nick ?? $"Player{userId}";
                var created = _options.FreshAccounts && userId != BotUser.UserId
                    ? _factory.BuildFresh(userId, name)
                    : _factory.BuildMax(userId, name);

                _normalizer.Normalize(created);
                _files.Save(path, created);
                return created;
            }

            if (_normalizer.Normalize(stored))
            {
                _files.Save(path, stored);
            }

            return stored;
        }
    }

    public void Save(int userId, JsonObject profile)
    {
        using (_lock.Enter())
        {
            _files.Save(PathFor(userId), profile);
        }
    }

    public JsonObject MutateData(int userId, Action<JsonObject> mutate)
    {
        using (_lock.Enter())
        {
            var profile = Json.CloneObject(Get(userId));
            var data = profile.EnsureObject("data");
            mutate(data);
            profile["result"] = 0;
            _files.Save(PathFor(userId), profile);
            return data;
        }
    }

    private string PathFor(int userId) => Path.Combine(_paths.State, "profiles", $"{userId}.json");
}
