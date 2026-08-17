using CW.Server.Configuration;
using CW.Server.Infrastructure;
using CW.Server.Services;
using CW.Server.Storage;
using CW.Server.Transport;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace CW.Server.Endpoints;

public sealed class AccountEndpoints
{
    private readonly IAccountRepository _accounts;
    private readonly ISessionRegistry _sessions;
    private readonly IProfileRepository _profiles;
    private readonly IClanRepository _clans;
    private readonly ICustomizationRepository _customization;
    private readonly IAttemptsRepository _attempts;
    private readonly IHostRegistry _hosts;
    private readonly PlayerService _players;
    private readonly ProfileNormalizer _normalizer;
    private readonly Data.GameCatalog _catalog;
    private readonly ServerOptions _options;
    private readonly IClock _clock;
    private readonly ILogger<AccountEndpoints> _logger;

    public AccountEndpoints(
        IAccountRepository accounts,
        ISessionRegistry sessions,
        IProfileRepository profiles,
        IClanRepository clans,
        ICustomizationRepository customization,
        IAttemptsRepository attempts,
        IHostRegistry hosts,
        PlayerService players,
        ProfileNormalizer normalizer,
        Data.GameCatalog catalog,
        IOptions<ServerOptions> options,
        IClock clock,
        ILogger<AccountEndpoints> logger)
    {
        _accounts = accounts;
        _sessions = sessions;
        _profiles = profiles;
        _clans = clans;
        _customization = customization;
        _attempts = attempts;
        _hosts = hosts;
        _players = players;
        _normalizer = normalizer;
        _catalog = catalog;
        _options = options.Value;
        _clock = clock;
        _logger = logger;
    }

    public JsonNode Init(LegacyRequest request)
    {
        var email = request.Text("email").Trim();
        var password = request.Text("password");

        var login = _accounts.LoginOrRegister(email, password);
        var token = _sessions.Create(login.UserId);
        _profiles.Get(login.UserId, _accounts.ByUserId(login.UserId)?.Nick);

        if (login.Created)
        {
            _logger.LogInformation(
                "registered account {Email} as user_id {UserId} ({Mode})",
                email,
                login.UserId,
                _options.FreshAccounts ? "fresh" : "maxed");
        }

        return new JsonObject
        {
            ["data"] = true,
            ["hash"] = RandomHash(),
            ["result"] = 0,
            ["ss"] = token,
            ["message"] = null,
            ["time"] = _clock.UnixSeconds.ToString(),
            ["user_id"] = login.UserId,
            ["updated"] = true,
            ["sessionHash"] = RandomHash(),
        };
    }

    public JsonNode Load(LegacyRequest request)
    {
        var userId = _players.Caller(request);
        if (userId is null)
        {
            return Reply.Fail("no session");
        }

        var profile = _profiles.Get(userId.Value);

        return _options.UnlockAll
            ? ProfileFactory.ApplyUnlockAll(Json.CloneObject(profile))
            : profile;
    }

    public JsonNode Save(LegacyRequest request)
    {
        var userId = _players.Caller(request);

        if (userId is not null && request.Body is JsonObject body)
        {
            var profile = _profiles.Get(userId.Value);
            var incoming = body.Obj("data");
            var target = profile.EnsureObject("data");

            foreach (var key in ClientOwnedFields)
            {
                if (incoming.TryGetPropertyValue(key, out var value))
                {
                    target[key] = Json.Clone(value);
                }
            }

            _profiles.Save(userId.Value, profile);
        }

        return new JsonObject { ["data"] = true, ["result"] = 0, ["message"] = null };
    }

    public JsonNode IdLoad(LegacyRequest request)
    {
        var userId = _players.Caller(request);
        if (userId is null)
        {
            return Reply.Fail("unknown user");
        }

        var profile = Json.CloneObject(_profiles.Get(userId.Value));
        profile["result"] = 0;

        if (_options.UnlockAll)
        {
            ProfileFactory.ApplyUnlockAll(profile);
        }

        if (profile["data"] is JsonObject data)
        {
            data["permission"] = 3;
        }

        return profile;
    }

    public JsonNode IdSave(LegacyRequest request)
    {
        var userId = _players.Caller(request);

        if (userId is null || request.Body is not JsonObject body || body.Count == 0)
        {
            return new JsonObject { ["data"] = true, ["result"] = 0 };
        }

        var stored = _profiles.Get(userId.Value);
        var priorEarn = Json.ToInt(stored.Obj("data")["clan_earn"]);
        var merged = Json.CloneObject(stored);

        foreach (var pair in body)
        {
            if (pair.Key == "data" && pair.Value is JsonObject incoming && merged["data"] is JsonObject target)
            {
                foreach (var field in incoming)
                {
                    if (field.Key != "podgon")
                    {
                        target[field.Key] = Json.Clone(field.Value);
                    }
                }
            }
            else
            {
                merged[pair.Key] = Json.Clone(pair.Value);
            }
        }

        merged["result"] = 0;
        _normalizer.Normalize(merged);

        var mergedData = merged.Obj("data");
        var deltaXp = Json.ToInt(mergedData["delta_xp"]);
        var deltaCr = Json.ToInt(mergedData["delta_cr"]);
        var share = Json.ToDouble(mergedData["clan_earn_proc"]);

        var earnedExp = deltaXp > 0 && share > 0 ? (int)(deltaXp * share) : 0;
        var earnedCr = deltaCr > 0 && share > 0 ? (int)(deltaCr * share) : 0;

        if (earnedExp > 0 && merged["data"] is JsonObject writable)
        {
            writable["clan_earn"] = priorEarn + earnedExp;
        }

        _profiles.Save(userId.Value, merged);

        if (earnedExp > 0 || earnedCr > 0)
        {
            var (clanId, clan) = _clans.ClanOf(userId.Value);

            if (clan is not null && clanId is not null)
            {
                _clans.Mutate(data =>
                {
                    if (data[clanId] is not JsonObject entry)
                    {
                        return;
                    }

                    entry["clan_exp"] = Json.ToInt(entry["clan_exp"]) + earnedExp;
                    entry["clan_cr"] = Json.ToInt(entry["clan_cr"]) + earnedCr;
                });

                _logger.LogInformation(
                    "clan {ClanId} credited +{Exp} exp +{Credits} cr from uid{UserId}",
                    clanId,
                    earnedExp,
                    earnedCr,
                    userId.Value);
            }
        }

        return new JsonObject { ["data"] = true, ["result"] = 0 };
    }

    public JsonNode KeepAlive(LegacyRequest request)
    {
        return new JsonObject { ["data"] = true, ["result"] = 0 };
    }

    public JsonNode GetAttempts(LegacyRequest request)
    {
        var userId = _players.Caller(request) ?? 0;
        return Reply.Ok(("attempts", _attempts.Get(userId, _catalog.DailyAttempts)));
    }

    public JsonNode RecordFriends(LegacyRequest request)
    {
        var userId = _players.Caller(request);

        if (userId is not null && request.Body is not null)
        {
            _customization.SaveFriends(userId.Value, request.Body);
        }

        return Reply.Ok();
    }

    public JsonNode ContentInfo(LegacyRequest request)
    {
        return new JsonObject { ["result"] = 0, ["server"] = _options.CdnHost };
    }

    public JsonNode CheckNick(LegacyRequest request)
    {
        var nick = request.Text("nick").Trim();

        if (nick.Length < 3 || _accounts.NickTaken(nick))
        {
            return new JsonObject { ["result"] = 0, ["error"] = "Nickname exist" };
        }

        return Reply.Fail("failed");
    }

    public JsonNode GetHosts(LegacyRequest request)
    {
        return HostList();
    }

    public JsonNode MasterServerRegister(LegacyRequest request)
    {
        var info = request.Body as JsonObject ?? new JsonObject();

        if (info["host"] is JsonObject nested)
        {
            info = nested;
        }

        var host = _hosts.Register(info, request.ClientIp);

        _logger.LogInformation(
            "host registered {Name} {Ip}:{Port} map={Map} mode={Mode}",
            Json.ToText(host["name"]),
            Json.ToText(host["ip"]),
            Json.ToText(host["port"]),
            Json.ToText(host["mapIndex"]),
            Json.ToText(host["gameMode"]));

        return Reply.Ok();
    }

    public JsonNode MasterServerList(LegacyRequest request)
    {
        return HostList();
    }

    private JsonObject HostList()
    {
        var hosts = new JsonArray();

        foreach (var host in _hosts.Live())
        {
            hosts.Add(Json.CloneObject(host));
        }

        return new JsonObject { ["hosts"] = hosts, ["updated"] = true };
    }

    private static string RandomHash()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    }

    private static readonly string[] ClientOwnedFields =
    {
        "settings", "suitNameIndex", "selectedSet", "nickname_color",
        "info0", "info1", "info2", "info3", "info4",
    };
}
