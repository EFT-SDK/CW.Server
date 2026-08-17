using CW.Server.Configuration;
using CW.Server.Infrastructure;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace CW.Server.Storage;

public interface ISessionRegistry
{
    string Create(int userId);

    IReadOnlyList<KeyValuePair<string, int>> Snapshot();

    int? LastUserId { get; }

    int SessionCount { get; }

    int? SingleActiveUser { get; }
}

public sealed class SessionRegistry : ISessionRegistry
{
    private const string TokenAlphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
    private const int TokenLength = 10;

    private readonly ServerPaths _paths;
    private readonly IJsonFileStore _files;
    private readonly StateLock _lock;
    private readonly IAccountRepository _accounts;

    private readonly Dictionary<string, int> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<int, string> _byUser = new();
    private int? _lastUserId;

    public SessionRegistry(
        ServerPaths paths,
        IJsonFileStore files,
        StateLock stateLock,
        IAccountRepository accounts)
    {
        _paths = paths;
        _files = files;
        _lock = stateLock;
        _accounts = accounts;

        Restore();
    }

    public int? LastUserId
    {
        get
        {
            using (_lock.Enter())
            {
                return _lastUserId;
            }
        }
    }

    public int SessionCount
    {
        get
        {
            using (_lock.Enter())
            {
                return _sessions.Count;
            }
        }
    }

    public int? SingleActiveUser
    {
        get
        {
            using (_lock.Enter())
            {
                return _byUser.Count == 1 ? _byUser.Keys.First() : null;
            }
        }
    }

    public string Create(int userId)
    {
        var token = NewToken();

        using (_lock.Enter())
        {
            if (_byUser.TryGetValue(userId, out var previous))
            {
                _sessions.Remove(previous);
            }

            _sessions[token] = userId;
            _byUser[userId] = token;
            _lastUserId = userId;
            Persist();
        }

        return token;
    }

    public IReadOnlyList<KeyValuePair<string, int>> Snapshot()
    {
        using (_lock.Enter())
        {
            return _sessions.ToList();
        }
    }

    private void Restore()
    {
        var known = _accounts.All().Select(a => a.UserId).ToHashSet();
        known.Add(BotUser.UserId);

        var blob = _files.LoadObject(_paths.StateFile("sessions.json"));
        var dropped = 0;

        foreach (var pair in blob.Obj("sessions"))
        {
            var userId = Json.ToInt(pair.Value, int.MinValue);
            if (userId == int.MinValue)
            {
                continue;
            }

            if (!known.Contains(userId))
            {
                dropped++;
                continue;
            }

            _sessions[pair.Key] = userId;
            _byUser[userId] = pair.Key;
        }

        var last = blob["last_uid"] is null ? (int?)null : Json.ToInt(blob["last_uid"]);
        _lastUserId = last.HasValue && known.Contains(last.Value) ? last : null;

        if (dropped > 0 || (last.HasValue && !_lastUserId.HasValue))
        {
            Persist();
        }
    }

    private void Persist()
    {
        var sessions = new JsonObject();
        foreach (var pair in _sessions)
        {
            sessions[pair.Key] = pair.Value;
        }

        _files.Save(_paths.StateFile("sessions.json"), new JsonObject
        {
            ["sessions"] = sessions,
            ["last_uid"] = _lastUserId is null ? null : JsonValue.Create(_lastUserId.Value),
        });
    }

    private static string NewToken()
    {
        return string.Create(TokenLength, 0, static (span, _) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = TokenAlphabet[RandomNumberGenerator.GetInt32(TokenAlphabet.Length)];
            }
        });
    }
}

public static class BotUser
{
    public const int UserId = -999;
}

public static class RequestSignature
{
    public static string Compute(string sessionToken, string uri, string post)
    {
        var payload = Encoding.UTF8.GetBytes(sessionToken + uri + post);
        return Convert.ToHexString(MD5.HashData(payload)).ToLowerInvariant();
    }
}
