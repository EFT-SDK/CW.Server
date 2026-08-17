using CW.Server.Data;
using CW.Server.Infrastructure;
using CW.Server.Services;
using CW.Server.Storage;
using CW.Server.Transport;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace CW.Server.Endpoints;

public sealed class PlayerServiceEndpoints
{
    private const int CurrencyCredits = 1;
    private const int CurrencyGamePoints = 2;
    private const int DailyBonusCredits = 1000;

    private readonly PlayerService _players;
    private readonly IProfileRepository _profiles;
    private readonly ITransactionLedger _ledger;
    private readonly GameCatalog _catalog;
    private readonly IClock _clock;

    public PlayerServiceEndpoints(
        PlayerService players,
        IProfileRepository profiles,
        ITransactionLedger ledger,
        GameCatalog catalog,
        IClock clock)
    {
        _players = players;
        _profiles = profiles;
        _ledger = ledger;
        _catalog = catalog;
        _clock = clock;
    }

    public JsonNode GetTransactions(LegacyRequest request)
    {
        var userId = request.Int("user_id");

        if (userId == 0)
        {
            userId = _players.Caller(request) ?? 0;
        }

        return new JsonObject
        {
            ["data"] = true,
            ["result"] = 0,
            ["transactions"] = _ledger.Read(userId),
        };
    }

    public JsonNode GetBalance(LegacyRequest request)
    {
        var balances = _players.Live(_players.Caller(request));

        return new JsonObject
        {
            ["result"] = 0,
            ["cr"] = balances.Credits.ToString(),
            ["sp"] = balances.SkillPoints.ToString(),
            ["gp"] = balances.GamePoints.ToString(),
        };
    }

    public JsonNode BuyGamePoints(LegacyRequest request)
    {
        var sku = request.Text("sku");
        var token = Md5(sku + _clock.UnixSeconds.ToString(CultureInfo.InvariantCulture));

        return new JsonObject
        {
            ["result"] = 1,
            ["url"] = $"gw-01.contractwarsgame.com/xsolla/payment.php?token={token}",
        };
    }

    public JsonNode PaymentsPending(LegacyRequest request)
    {
        return Reply.Ok(("payments_pending", 0));
    }

    public JsonNode ClearNewLevel(LegacyRequest request)
    {
        var userId = _players.Caller(request);

        if (userId is not null)
        {
            var profile = _profiles.Get(userId.Value);
            profile["newLevel"] = 0;
            profile["showXPBonus"] = false;
            _profiles.Save(userId.Value, profile);
        }

        return Reply.Ok();
    }

    public JsonNode Promo(LegacyRequest request)
    {
        return Reply.Fail("Promo code is not valid");
    }

    public JsonNode ResetSkills(LegacyRequest request)
    {
        var userId = _players.Caller(request);
        var payWithCredits = request.Action.Contains("cr", StringComparison.Ordinal);

        if (userId is null)
        {
            return Reply.Ok();
        }

        var data = _players.Data(userId.Value);
        var spent = data.Arr("skills").OfType<JsonObject>().Sum(s => Json.ToInt(s["level"]));
        var price = payWithCredits ? _catalog.SkillResetCr : 0;
        var field = payWithCredits ? PlayerService.Credits : PlayerService.GamePoints;

        if (payWithCredits && Json.ToInt(data["cr"]) < price)
        {
            return Reply.Fail("failed");
        }

        _players.MutateData(userId.Value, profile =>
        {
            foreach (var skill in profile.Arr("skills").OfType<JsonObject>())
            {
                skill["level"] = 0;
            }

            profile["sp"] = Json.ToInt(profile["sp"]) + spent;

            if (price != 0)
            {
                profile[field] = Json.ToInt(profile[field]) - price;
            }
        });

        if (price != 0)
        {
            _ledger.Record(userId, payWithCredits ? CurrencyCredits : CurrencyGamePoints, -price, "SKILLS RESET");
        }

        return Reply.Ok();
    }

    public JsonNode SkillRent(LegacyRequest request)
    {
        var userId = _players.Caller(request);

        if (userId is null)
        {
            return Reply.Fail("no session");
        }

        var skillId = FirstInt(request, -1, "skill_index", "skill_id");
        var option = FirstInt(request, -1, "rentOption", "rent_option");

        if (skillId < 0 || skillId >= _catalog.Skills.Count)
        {
            return Reply.Fail("unknown skill");
        }

        var skill = _catalog.Skills[skillId] as JsonObject ?? new JsonObject();
        var prices = skill.Arr("rentPrice");
        var days = skill.Arr("rentTime");

        if (option < 0 || option >= prices.Count)
        {
            return Reply.Fail("bad rent option");
        }

        var premium = Json.ToBool(skill["isPremium"]);
        var field = premium ? PlayerService.GamePoints : PlayerService.Credits;
        var price = Json.ToInt(prices[option]);
        var rentDays = option < days.Count ? Math.Max(1, Json.ToInt(days[option], 1)) : 1;
        var rentEnd = _clock.UnixSeconds + rentDays * 86400L;

        var denied = false;
        var credits = 0;
        var gamePoints = 0;

        _players.MutateData(userId.Value, data =>
        {
            if (Json.ToInt(data[field]) < price)
            {
                denied = true;
                return;
            }

            data[field] = Json.ToInt(data[field]) - price;

            var target = PlayerService.SkillAt(data, skillId);
            if (target is not null)
            {
                target["unlocked"] = true;
                target["rentEnd"] = rentEnd;
            }

            credits = Json.ToInt(data["cr"]);
            gamePoints = Json.ToInt(data["gp"]);
        });

        if (denied)
        {
            return Reply.Fail("Not enough " + field.ToUpperInvariant());
        }

        _ledger.Record(
            userId,
            premium ? CurrencyGamePoints : CurrencyCredits,
            -price,
            $"SKILL RENT ({skillId}) {rentDays}d");

        var live = _players.Live(userId);

        return Reply.Ok(
            ("premiumBuy", true),
            ("skill_index", skillId),
            ("rentEnd", rentEnd),
            ("new_gp", gamePoints),
            ("new_cr", credits),
            ("new_sp", live.SkillPoints));
    }

    public JsonNode GetContracts(LegacyRequest request)
    {
        var userId = _players.Caller(request);

        return Reply.Ok(
            ("user_id", (userId ?? 0).ToString()),
            ("contracts", ContractsOf(userId)));
    }

    public JsonNode InitContracts(LegacyRequest request)
    {
        var userId = _players.Caller(request);
        var state = BlankContracts(userId);

        if (userId is not null)
        {
            _players.MutateData(userId.Value, data => data["contracts"] = Json.CloneObject(state));
        }

        return ContractEnvelope(userId, state);
    }

    public JsonNode PerformContracts(LegacyRequest request)
    {
        var userId = _players.Caller(request);
        return ContractEnvelope(userId, ContractsOf(userId));
    }

    public JsonNode DailyBonus(LegacyRequest request)
    {
        var userId = _players.Caller(request);

        if (userId is null)
        {
            return Reply.Fail("failed");
        }

        var today = _clock.Now.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture);

        if (Json.ToText(_players.Data(userId.Value)["dailyBonusDate"]) == today)
        {
            return Reply.Fail("already taken");
        }

        var credits = 0;
        var gamePoints = 0;
        var skillPoints = 0;
        var battleGold = 0;

        _players.MutateData(userId.Value, data =>
        {
            data["dailyBonusDate"] = today;
            credits = PlayerService.Grant(data, PlayerService.Credits, DailyBonusCredits);
            gamePoints = Json.ToInt(data["gp"]);
            skillPoints = Json.ToInt(data["sp"]);
            battleGold = Json.ToInt(data["bg"]);
        });

        _ledger.Record(userId, CurrencyCredits, DailyBonusCredits, "DAILY BONUS");

        return Reply.Ok(
            ("day", 1),
            ("weapon_id", -1),
            ("rent_time", 0),
            ("new_cr", credits),
            ("new_gp", gamePoints),
            ("new_sp", skillPoints),
            ("new_bg", battleGold));
    }

    private JsonObject ContractsOf(int? userId)
    {
        if (userId is null)
        {
            return BlankContracts(null);
        }

        var stored = _players.Data(userId.Value)["contracts"];
        return stored is JsonObject contracts ? Json.CloneObject(contracts) : BlankContracts(userId);
    }

    private JsonObject BlankContracts(int? userId)
    {
        return new JsonObject
        {
            ["user_id"] = (userId ?? 0).ToString(),
            ["easy_counter"] = 0,
            ["normal_counter"] = 0,
            ["hard_counter"] = 0,
            ["current_easy"] = 0,
            ["current_normal"] = 0,
            ["current_hard"] = 0,
            ["timer_end"] = _clock.UnixSeconds + _catalog.ContractsPeriodSeconds,
        };
    }

    private static JsonObject ContractEnvelope(int? userId, JsonObject state)
    {
        var payload = Reply.Ok(
            ("user_id", (userId ?? 0).ToString()),
            ("contracts", Json.CloneObject(state)));

        foreach (var pair in state)
        {
            payload[pair.Key] = Json.Clone(pair.Value);
        }

        return payload;
    }

    private static int FirstInt(LegacyRequest request, int fallback, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (request.Contains(key))
            {
                return request.Int(key, fallback);
            }
        }

        return fallback;
    }

    private static string Md5(string input)
    {
        return Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
    }
}
