using CW.Server.Configuration;
using CW.Server.Infrastructure;
using System.Text.Json.Nodes;

namespace CW.Server.Storage;

public interface IWatchlistRepository
{
    JsonArray Read(int userId);

    void Write(int userId, JsonArray ids);
}

public sealed class WatchlistRepository : IWatchlistRepository
{
    private readonly ServerPaths _paths;
    private readonly IJsonFileStore _files;
    private readonly StateLock _lock;

    public WatchlistRepository(ServerPaths paths, IJsonFileStore files, StateLock stateLock)
    {
        _paths = paths;
        _files = files;
        _lock = stateLock;
    }

    public JsonArray Read(int userId)
    {
        using (_lock.Enter())
        {
            return _files.LoadArray(PathFor(userId));
        }
    }

    public void Write(int userId, JsonArray ids)
    {
        using (_lock.Enter())
        {
            _files.Save(PathFor(userId), ids);
        }
    }

    private string PathFor(int userId) => Path.Combine(_paths.State, "watchlist", $"{userId}.json");
}
