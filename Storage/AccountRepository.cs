using CW.Server.Configuration;
using CW.Server.Infrastructure;
using System.Text.Json.Nodes;

namespace CW.Server.Storage;

public sealed record AccountRecord(string Key, int UserId, string Nick, string Password, long Created);

public sealed record LoginResult(int UserId, bool Created);

public interface IAccountRepository
{
    LoginResult LoginOrRegister(string email, string password);

    bool NickTaken(string nick);

    AccountRecord? ByUserId(int userId);

    IReadOnlyCollection<AccountRecord> All();

    void UpdateNick(int userId, string nick);

    int NextUserId { get; }

    int Count { get; }
}

public sealed class AccountRepository : IAccountRepository
{
    private readonly ServerPaths _paths;
    private readonly IJsonFileStore _files;
    private readonly StateLock _lock;
    private readonly IClock _clock;

    private readonly Dictionary<string, JsonObject> _accounts = new(StringComparer.Ordinal);
    private int _nextUserId;

    public AccountRepository(ServerPaths paths, IJsonFileStore files, StateLock stateLock, IClock clock)
    {
        _paths = paths;
        _files = files;
        _lock = stateLock;
        _clock = clock;

        var stored = _files.LoadObject(_paths.StateFile("accounts.json"));
        foreach (var pair in stored)
        {
            if (pair.Value is JsonObject account)
            {
                _accounts[pair.Key] = Json.CloneObject(account);
            }
        }

        _nextUserId = Math.Max(1, Json.ToInt(_files.Load(_paths.StateFile("next_uid.json")), 1));

        if (_accounts.Count > 0)
        {
            var highest = _accounts.Values.Max(a => Json.ToInt(a["user_id"]));
            _nextUserId = Math.Max(_nextUserId, highest + 1);
        }
    }

    public int NextUserId
    {
        get
        {
            using (_lock.Enter())
            {
                return _nextUserId;
            }
        }
    }

    public int Count
    {
        get
        {
            using (_lock.Enter())
            {
                return _accounts.Count;
            }
        }
    }

    public LoginResult LoginOrRegister(string email, string password)
    {
        var key = (email ?? string.Empty).Trim().ToLowerInvariant();
        if (key.Length == 0)
        {
            key = "anonymous";
        }

        using (_lock.Enter())
        {
            if (_accounts.TryGetValue(key, out var existing))
            {
                if (!string.IsNullOrEmpty(password) && Json.ToText(existing["password"]) != password)
                {
                    existing["password"] = password;
                    Persist();
                }

                return new LoginResult(Json.ToInt(existing["user_id"]), false);
            }

            var userId = _nextUserId;
            _nextUserId++;

            var nick = DeriveNick(email);
            _accounts[key] = new JsonObject
            {
                ["user_id"] = userId,
                ["password"] = password ?? string.Empty,
                ["nick"] = nick,
                ["created"] = _clock.UnixSeconds,
            };

            Persist();
            return new LoginResult(userId, true);
        }
    }

    public bool NickTaken(string nick)
    {
        var needle = (nick ?? string.Empty).Trim().ToLowerInvariant();

        using (_lock.Enter())
        {
            return _accounts.Values.Any(a => Json.ToText(a["nick"]).Trim().ToLowerInvariant() == needle);
        }
    }

    public AccountRecord? ByUserId(int userId)
    {
        using (_lock.Enter())
        {
            foreach (var pair in _accounts)
            {
                if (Json.ToInt(pair.Value["user_id"]) == userId)
                {
                    return Map(pair.Key, pair.Value);
                }
            }
        }

        return null;
    }

    public IReadOnlyCollection<AccountRecord> All()
    {
        using (_lock.Enter())
        {
            return _accounts.Select(p => Map(p.Key, p.Value)).ToList();
        }
    }

    public void UpdateNick(int userId, string nick)
    {
        using (_lock.Enter())
        {
            foreach (var account in _accounts.Values)
            {
                if (Json.ToInt(account["user_id"]) == userId)
                {
                    account["nick"] = nick;
                    Persist();
                    return;
                }
            }
        }
    }

    private void Persist()
    {
        var payload = new JsonObject();
        foreach (var pair in _accounts)
        {
            payload[pair.Key] = Json.CloneObject(pair.Value);
        }

        _files.Save(_paths.StateFile("accounts.json"), payload);
        _files.Save(_paths.StateFile("next_uid.json"), JsonValue.Create(_nextUserId));
    }

    private static AccountRecord Map(string key, JsonObject account)
    {
        return new AccountRecord(
            key,
            Json.ToInt(account["user_id"]),
            Json.ToText(account["nick"]),
            Json.ToText(account["password"]),
            Json.ToLong(account["created"]));
    }

    private static string DeriveNick(string? email)
    {
        var source = string.IsNullOrWhiteSpace(email) ? "Player" : email;
        var local = source.Split('@')[0];
        var trimmed = local.Length > 16 ? local[..16] : local;
        return trimmed.Length == 0 ? "Player" : trimmed;
    }
}
