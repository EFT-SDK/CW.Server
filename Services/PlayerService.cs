using CW.Server.Infrastructure;
using CW.Server.Storage;
using CW.Server.Transport;
using System.Text.Json.Nodes;

namespace CW.Server.Services;

public sealed record Balances(int Credits, int GamePoints, int SkillPoints, int BattleGold, int RenameCount);

public sealed record Purchase(bool Success, int NewBalance)
{
    public static Purchase Denied => new(false, 0);
}

public sealed class PlayerService
{
    public const string Credits = "cr";
    public const string GamePoints = "gp";
    public const string SkillPoints = "sp";
    public const string BattleGold = "bg";

    private readonly IProfileRepository _profiles;
    private readonly ICallerResolver _callers;

    public PlayerService(IProfileRepository profiles, ICallerResolver callers)
    {
        _profiles = profiles;
        _callers = callers;
    }

    public int? Caller(LegacyRequest request) => _callers.Resolve(request);

    public int? CallerIgnoringQuery(LegacyRequest request)
    {
        var stripped = request.Query
            .Where(p => p.Key is not ("user_id" or "uid"))
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

        var scoped = new LegacyRequest
        {
            Method = request.Method,
            Path = request.Path,
            RawQuery = request.RawQuery,
            Query = stripped,
            RawBody = request.RawBody,
            Body = request.Body,
            ClientIp = request.ClientIp,
        };

        return _callers.Resolve(scoped);
    }

    public Balances Live(int? userId)
    {
        if (userId is null)
        {
            return new Balances(0, 0, 0, 0, 0);
        }

        var data = _profiles.Get(userId.Value).Obj("data");

        return new Balances(
            Json.ToInt(data["cr"]),
            Json.ToInt(data["gp"]),
            Json.ToInt(data["sp"]),
            Json.ToInt(data["bg"]),
            Json.ToInt(data["renameCount"]));
    }

    public JsonObject Data(int userId) => _profiles.Get(userId).Obj("data");

    public JsonObject MutateData(int userId, Action<JsonObject> mutate) => _profiles.MutateData(userId, mutate);

    public Purchase Charge(int userId, string currencyField, int price, Action<JsonObject>? apply = null)
    {
        var success = false;
        var balance = 0;

        _profiles.MutateData(userId, data =>
        {
            var current = Json.ToInt(data[currencyField]);

            if (current < price)
            {
                balance = current;
                return;
            }

            balance = current - price;
            data[currencyField] = balance;
            apply?.Invoke(data);
            success = true;
        });

        return new Purchase(success, balance);
    }

    public static int Grant(JsonObject data, string currencyField, int amount)
    {
        var updated = Json.ToInt(data[currencyField]) + amount;
        data[currencyField] = updated;
        return updated;
    }

    public static JsonObject? WeaponAt(JsonObject data, int index)
    {
        var weapons = data.Arr("weapons");
        return index >= 0 && index < weapons.Count ? weapons[index] as JsonObject : null;
    }

    public static JsonObject? SkillAt(JsonObject data, int index)
    {
        var skills = data.Arr("skills");
        return index >= 0 && index < skills.Count ? skills[index] as JsonObject : null;
    }
}
