using CW.Server.Data;
using CW.Server.Infrastructure;
using CW.Server.Services;
using CW.Server.Storage;
using CW.Server.Transport;
using System.Text.Json.Nodes;

namespace CW.Server.Endpoints;

public sealed class EconomyEndpoints
{
    private const int UndestructableSentinel = -77;
    private const int CurrencyCredits = 1;
    private const int CurrencyGamePoints = 2;

    private readonly PlayerService _players;
    private readonly GameCatalog _catalog;
    private readonly ITransactionLedger _ledger;
    private readonly IClock _clock;
    private readonly ILogger<EconomyEndpoints> _logger;

    public EconomyEndpoints(
        PlayerService players,
        GameCatalog catalog,
        ITransactionLedger ledger,
        IClock clock,
        ILogger<EconomyEndpoints> logger)
    {
        _players = players;
        _catalog = catalog;
        _ledger = ledger;
        _clock = clock;
        _logger = logger;
    }

    public JsonNode WeaponUnlock(LegacyRequest request)
    {
        var userId = _players.Caller(request);
        if (userId is null)
        {
            return Reply.Fail("no session");
        }

        var index = FirstInt(request, -1, "weapon_index", "weapon_id", "index");
        var price = _catalog.WeaponCr.GetValueOrDefault(index);

        var purchase = _players.Charge(userId.Value, PlayerService.Credits, price, data =>
        {
            var weapon = PlayerService.WeaponAt(data, index);
            if (weapon is not null)
            {
                weapon["unlocked"] = true;
                weapon["repair_info"] = UndestructableSentinel;
            }
        });

        if (!purchase.Success)
        {
            return Reply.Fail("Not enough credits");
        }

        return Reply.Ok(
            ("premiumBuy", false),
            ("weapon_id", index),
            ("new_cr", purchase.NewBalance));
    }

    public JsonNode PremiumWeaponUnlock(LegacyRequest request)
    {
        var userId = _players.Caller(request);
        if (userId is null)
        {
            return Reply.Fail("no session");
        }

        var index = FirstInt(request, -1, "weapon_index", "weapon_id");
        var option = request.Int("rentOption");

        var rentPrices = _catalog.WeaponRentGp.GetValueOrDefault(index) ?? new List<int> { 0 };
        var rentDays = _catalog.WeaponRentDays.GetValueOrDefault(index) ?? new List<int>();
        var permanentPrice = _catalog.WeaponPermanentGp.GetValueOrDefault(index);

        var permanent = option >= rentDays.Count || option >= rentPrices.Count;
        int price;
        long rentEnd;

        if (permanent)
        {
            price = permanentPrice != 0
                ? permanentPrice
                : rentPrices.Count > 0 ? rentPrices[^1] : 0;
            rentEnd = -1;
        }
        else
        {
            price = rentPrices[option];
            rentEnd = _clock.UnixSeconds + Math.Max(1, rentDays[option]) * 86400L;
        }

        var purchase = _players.Charge(userId.Value, PlayerService.GamePoints, price, data =>
        {
            var weapon = PlayerService.WeaponAt(data, index);
            if (weapon is not null)
            {
                weapon["unlocked"] = true;
                weapon["repair_info"] = UndestructableSentinel;
                weapon["rentEnd"] = rentEnd;
            }
        });

        if (!purchase.Success)
        {
            return Reply.Fail("Not enough GP");
        }

        _ledger.Record(userId, CurrencyGamePoints, -price, $"WEAPON {(permanent ? "BUY" : "RENT")} ({index})");

        _logger.LogInformation(
            "weapon {Index} {Term} for {Price}gp uid{UserId}",
            index,
            permanent ? "permanent" : $"{rentDays[option]}d",
            price,
            userId.Value);

        return Reply.Ok(
            ("premiumBuy", true),
            ("weapon_id", index),
            ("newGP", purchase.NewBalance),
            ("rentEnd", rentEnd));
    }

    public JsonNode BuyKit(LegacyRequest request)
    {
        var userId = _players.Caller(request);
        if (userId is null)
        {
            return Reply.Fail("no session");
        }

        var index = FirstInt(request, 0, "kit_index", "set_index");
        var price = PriceAt(_catalog.KitGp, index);

        var purchase = _players.Charge(userId.Value, PlayerService.GamePoints, price,
            data => UnlockSet(data, index));

        if (!purchase.Success)
        {
            return Reply.Fail("Not enough GP");
        }

        return Reply.Ok(
            ("kit_index", index),
            ("set_index", index),
            ("new_gp", purchase.NewBalance));
    }

    public JsonNode BuySet(LegacyRequest request)
    {
        var userId = _players.Caller(request);
        if (userId is null)
        {
            return Reply.Fail("no session");
        }

        var index = request.Int("set_index");
        var price = PriceAt(_catalog.SetGp, index);

        var purchase = _players.Charge(userId.Value, PlayerService.GamePoints, price,
            data => UnlockSet(data, index));

        if (!purchase.Success)
        {
            return Reply.Fail("Not enough GP");
        }

        return Reply.Ok(("set_index", index), ("new_gp", purchase.NewBalance));
    }

    public JsonNode BuySkillPoint(LegacyRequest request)
    {
        var userId = _players.Caller(request);
        if (userId is null)
        {
            return Reply.Fail("no session");
        }

        var price = _catalog.SpGp;
        string? error = null;
        var gamePoints = 0;
        var skillPoints = 0;
        var remaining = 0;

        _players.MutateData(userId.Value, data =>
        {
            var available = Json.ToInt(data["sp_available"]);

            if (available <= 0)
            {
                error = "No SP purchases left";
                return;
            }

            if (Json.ToInt(data["gp"]) < price)
            {
                error = "Not enough GP";
                return;
            }

            gamePoints = Json.ToInt(data["gp"]) - price;
            skillPoints = Json.ToInt(data["sp"]) + 1;
            remaining = available - 1;

            data["gp"] = gamePoints;
            data["sp"] = skillPoints;
            data["sp_available"] = remaining;
        });

        if (error is not null)
        {
            return Reply.Fail(error);
        }

        _ledger.Record(userId, CurrencyGamePoints, -price, "+1 SP");

        return Reply.Ok(
            ("new_gp", gamePoints),
            ("new_sp", skillPoints),
            ("new_sp_available", remaining));
    }

    public JsonNode BuyNickChanges(LegacyRequest request)
    {
        var userId = _players.Caller(request);
        if (userId is null)
        {
            return Reply.Fail("no session");
        }

        var price = _catalog.NickGp;
        var renameCount = 0;

        var purchase = _players.Charge(userId.Value, PlayerService.GamePoints, price, data =>
        {
            renameCount = Json.ToInt(data["renameCount"]) + 1;
            data["renameCount"] = renameCount;
        });

        if (!purchase.Success)
        {
            return Reply.Fail("Not enough GP");
        }

        return Reply.Ok(("new_gp", purchase.NewBalance), ("renameCount", renameCount));
    }

    public JsonNode BuyWeaponTask(LegacyRequest request)
    {
        var userId = _players.Caller(request);
        if (userId is null)
        {
            return Reply.Fail("no session");
        }

        var index = FirstInt(request, -1, "weapon_index", "weapon_id");
        var price = _catalog.WeaponWtaskGp.GetValueOrDefault(index, 1000);

        var purchase = _players.Charge(userId.Value, PlayerService.GamePoints, price, data =>
        {
            var weapon = PlayerService.WeaponAt(data, index);
            if (weapon is not null)
            {
                weapon["wtaskCurrent"] = UndestructableSentinel;
            }
        });

        if (!purchase.Success)
        {
            return Reply.Fail("Not enough GP");
        }

        _ledger.Record(userId, CurrencyGamePoints, -price, $"WTASK BUY ({index})");
        _logger.LogInformation("wtask bought on w{Index} for {Price}gp uid{UserId}", index, price, userId.Value);

        return Reply.Ok(("weapon_index", index), ("new_gp", purchase.NewBalance));
    }

    public JsonNode BuyBox(LegacyRequest request)
    {
        var userId = _players.Caller(request);
        if (userId is null)
        {
            return Reply.Fail("no session");
        }

        var boxId = request.Int("box_id");
        var gpCost = _catalog.BoxGp.GetValueOrDefault(boxId);
        var crCost = _catalog.BoxCr.GetValueOrDefault(boxId);
        var items = _catalog.BoxItems.GetValueOrDefault(boxId) ?? new JsonArray();

        var creditReward = items
            .OfType<JsonObject>()
            .Where(item => Json.ToText(item["type"]) == "credits")
            .Sum(item => Json.ToInt(item["id"]));

        var denied = false;
        var gamePoints = 0;
        var credits = 0;

        _players.MutateData(userId.Value, data =>
        {
            if (Json.ToInt(data["gp"]) < gpCost || Json.ToInt(data["cr"]) < crCost)
            {
                denied = true;
                return;
            }

            gamePoints = Json.ToInt(data["gp"]) - gpCost;
            credits = Json.ToInt(data["cr"]) - crCost + creditReward;

            data["gp"] = gamePoints;
            data["cr"] = credits;
        });

        if (denied)
        {
            return Reply.Fail("Not enough funds");
        }

        var granted = new List<string>();
        var now = _clock.UnixSeconds;

        foreach (var item in items.OfType<JsonObject>())
        {
            var kind = Json.ToText(item["type"]);
            var itemId = Json.ToInt(item["id"]);
            var days = Json.ToInt(item["rentTime"]);
            var until = days < 0 ? -1L : now + Math.Max(1, days) * 86400L;

            switch (kind)
            {
                case "weapon":
                    RentWeapon(userId.Value, itemId, until);
                    granted.Add($"weapon {itemId}{(days < 0 ? " perm" : $" {days}d")}");
                    break;

                case "skill":
                    RentSkill(userId.Value, itemId, until);
                    granted.Add($"skill {itemId}{(days < 0 ? " perm" : $" {days}d")}");
                    break;

                case "credits":
                    granted.Add($"+{itemId} cr");
                    break;
            }
        }

        if (gpCost != 0)
        {
            _ledger.Record(userId, CurrencyGamePoints, -gpCost, $"BOX BUY ({boxId})");
        }

        if (crCost != 0)
        {
            _ledger.Record(userId, CurrencyCredits, -crCost, $"BOX BUY ({boxId})");
        }

        if (creditReward != 0)
        {
            _ledger.Record(userId, CurrencyCredits, creditReward, $"BOX REWARD ({boxId})");
        }

        _logger.LogInformation(
            "box {BoxId} opened uid{UserId}: {Granted}",
            boxId,
            userId.Value,
            granted.Count > 0 ? string.Join(", ", granted) : "empty");

        return Reply.Ok(("box_id", boxId), ("new_gp", gamePoints), ("new_cr", credits));
    }

    public JsonNode BuyCamouflage(LegacyRequest request, int camoId)
    {
        var userId = _players.Caller(request);
        if (userId is null)
        {
            return Reply.Fail("no session");
        }

        var pricing = _catalog.CamoPrices.GetValueOrDefault(camoId) ?? new CamoPrice(1, 0);
        var field = pricing.Currency == 2 ? PlayerService.GamePoints : PlayerService.Credits;

        var purchase = _players.Charge(userId.Value, field, pricing.Price);

        if (!purchase.Success)
        {
            return Reply.Fail("Not enough funds");
        }

        _logger.LogInformation(
            "camo {CamoId} bought uid{UserId} (-{Price} {Field})",
            camoId,
            userId.Value,
            pricing.Price,
            field);

        return Reply.Ok();
    }

    public JsonNode SkillUnlock(LegacyRequest request)
    {
        var userId = _players.Caller(request);
        var live = _players.Live(userId);

        if (userId is null)
        {
            return Reply.Fail("no session");
        }

        var index = request.Int("skill_index", -1);
        var cost = _catalog.SkillSp.GetValueOrDefault(index, 1);

        var credits = live.Credits;
        var gamePoints = live.GamePoints;
        var skillPoints = live.SkillPoints;

        _players.MutateData(userId.Value, data =>
        {
            var skill = PlayerService.SkillAt(data, index);
            if (skill is not null)
            {
                skill["unlocked"] = true;
            }

            skillPoints = Math.Max(0, Json.ToInt(data["sp"]) - cost);
            data["sp"] = skillPoints;

            credits = Json.ToInt(data["cr"]);
            gamePoints = Json.ToInt(data["gp"]);
        });

        _logger.LogInformation("skill {Index} unlocked uid{UserId} (-{Cost}sp)", index, userId.Value, cost);

        return Reply.Ok(
            ("error", string.Empty),
            ("user_id", userId.Value.ToString()),
            ("skill_index", index),
            ("rentEnd", 0),
            ("new_cr", credits),
            ("new_gp", gamePoints),
            ("new_sp", skillPoints));
    }

    public JsonNode Repair(LegacyRequest request)
    {
        var userId = _players.Caller(request);
        var live = _players.Live(userId);

        if (userId is null)
        {
            return Reply.Fail("no session");
        }

        var index = request.Int("weapon_index", -1);
        var amount = request.Int("amount");
        var credits = live.Credits;

        _players.MutateData(userId.Value, data =>
        {
            var weapon = PlayerService.WeaponAt(data, index);
            if (weapon is not null)
            {
                weapon["repair_info"] = UndestructableSentinel;
            }

            credits = Math.Max(0, Json.ToInt(data["cr"]) - amount);
            data["cr"] = credits;
        });

        return Reply.Ok(
            ("weapon_index", index),
            ("new_weapon_info", UndestructableSentinel),
            ("new_cr", credits));
    }

    public JsonNode BankBuy(LegacyRequest request)
    {
        var userId = _players.Caller(request);
        var currency = request.Int("cur", CurrencyGamePoints);
        var amount = request.Int("amount");

        if (amount < 1)
        {
            return Reply.Fail("Need to ban! amount < 1");
        }

        var field = currency == CurrencyGamePoints ? PlayerService.GamePoints : PlayerService.Credits;
        var updated = amount;

        if (userId is not null)
        {
            _players.MutateData(userId.Value, data => updated = PlayerService.Grant(data, field, amount));
        }

        return new JsonObject
        {
            ["result"] = 0,
            ["currency"] = currency,
            ["new_amount"] = updated,
        };
    }

    public JsonNode SkipContract(LegacyRequest request)
    {
        return Reply.Ok(("msg", string.Empty));
    }

    public void RentWeapon(int userId, int weaponId, long until)
    {
        _players.MutateData(userId, data =>
        {
            foreach (var weapon in data.Arr("weapons").OfType<JsonObject>())
            {
                if (Json.ToInt(weapon["id"]) == weaponId)
                {
                    weapon["unlocked"] = true;
                    weapon["rentEnd"] = until;
                    return;
                }
            }
        });
    }

    public void RentSkill(int userId, int skillId, long until)
    {
        _players.MutateData(userId, data =>
        {
            var skill = PlayerService.SkillAt(data, skillId);
            if (skill is not null)
            {
                skill["unlocked"] = true;
                skill["rentEnd"] = until;
            }
        });
    }

    public void GrantClanSkill(int userId, int skillId, long rentEnd)
    {
        _players.MutateData(userId, data =>
        {
            var skills = data.EnsureArray("clan_skills");

            while (skills.Count <= skillId)
            {
                skills.Add(new JsonObject { ["unlocked"] = false, ["rentEnd"] = 0 });
            }

            if (skills[skillId] is not JsonObject entry)
            {
                entry = new JsonObject();
                skills[skillId] = entry;
            }

            entry["unlocked"] = true;
            entry["rentEnd"] = rentEnd;
        });
    }

    private static void UnlockSet(JsonObject data, int index)
    {
        var sets = data.Arr("unlockedSets");
        if (index >= 0 && index < sets.Count)
        {
            sets[index] = 1;
        }
    }

    private static int PriceAt(IReadOnlyList<int> prices, int index)
    {
        return index >= 0 && index < prices.Count ? prices[index] : 0;
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
}
