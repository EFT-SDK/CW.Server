using CW.Server.Configuration;
using CW.Server.Infrastructure;

namespace CW.Server.Storage;

public interface IAttemptsRepository
{
    int Get(int userId, int fallback);

    int Set(int userId, int value);
}

public sealed class AttemptsRepository : IAttemptsRepository
{
    private readonly ServerPaths _paths;
    private readonly IJsonFileStore _files;
    private readonly StateLock _lock;
    private readonly Dictionary<int, int> _cache = new();

    public AttemptsRepository(ServerPaths paths, IJsonFileStore files, StateLock stateLock)
    {
        _paths = paths;
        _files = files;
        _lock = stateLock;
    }

    public int Get(int userId, int fallback)
    {
        using (_lock.Enter())
        {
            if (_cache.TryGetValue(userId, out var cached))
            {
                return cached;
            }

            var stored = _files.LoadObject(Path());
            var value = stored.TryGetPropertyValue(userId.ToString(), out var node)
                ? Json.ToInt(node, fallback)
                : fallback;

            _cache[userId] = value;
            return value;
        }
    }

    public int Set(int userId, int value)
    {
        using (_lock.Enter())
        {
            _cache[userId] = value;
            var stored = _files.LoadObject(Path());
            stored[userId.ToString()] = value;
            _files.Save(Path(), stored);
            return value;
        }
    }

    private string Path() => _paths.StateFile("attempts.json");
}
