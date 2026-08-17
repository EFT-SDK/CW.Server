using CW.Server.Data;
using CW.Server.Infrastructure;
using CW.Server.Services;
using CW.Server.Storage;
using CW.Server.Transport;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace CW.Server.Endpoints;

public sealed record RouletteOutcome(string Kind, int Index, int Amount, double Chance);

public sealed class RouletteEndpoints
{
    private readonly PlayerService _players;
    private readonly GameCatalog _catalog;
    private readonly IAttemptsRepository _attempts;
    private readonly ICustomizationRepository _customization;
    private readonly MasteringService _mastering;
    private readonly EconomyEndpoints _economy;
    private readonly IClock _clock;
    private readonly ILogger<RouletteEndpoints> _logger;

    public RouletteEndpoints(
        PlayerService players,
        GameCatalog catalog,
        IAttemptsRepository attempts,
        ICustomizationRepository customization,
        MasteringService mastering,
        EconomyEndpoints economy,
        IClock clock,
        ILogger<RouletteEndpoints> logger)
    {
        _players = players;
        _catalog = catalog;
        _attempts = attempts;
        _customization = customization;
        _mastering = mastering;
        _economy = economy;
        _clock = clock;
        _logger = logger;
    }

    public JsonNode Spin(LegacyRequest request)
    {
        var userId = _players.Caller(request) ?? 0;
        var remaining = Math.Max(0, _attempts.Get(userId, _catalog.DailyAttempts) - 1);
        _attempts.Set(userId, remaining);

        var outcome = Pick();
        var level = 1;

        if (userId != 0)
        {
            level = Math.Max(1, _catalog.LevelForXp(Json.ToInt(_players.Data(userId)["currentXP"])));
        }

        var now = _clock.UnixSeconds;
        string index;

        switch (outcome.Kind)
        {
            case "camo":
                {
                    var camoIds = _catalog.CamoPrices.Keys.OrderBy(x => x).ToList();
                    if (camoIds.Count == 0)
                    {
                        camoIds.Add(60);
                    }

                    var weapons = _mastering.Load(userId)
                        .Obj("mod_states")
                        .Select(p => Json.ParseInt(p.Key))
                        .OrderBy(x => x)
                        .ToList();

                    if (weapons.Count == 0)
                    {
                        weapons.Add(1);
                    }

                    var camoId = camoIds[RandomNumberGenerator.GetInt32(camoIds.Count)];
                    var weaponId = weapons[RandomNumberGenerator.GetInt32(weapons.Count)];
                    index = $"camo-{camoId}-{weaponId}";

                    if (userId != 0)
                    {
                        _customization.UnlockCamo(userId, weaponId, camoId);
                    }

                    break;
                }

            case "weapon":
                {
                    var choices = _catalog.WeaponCr.Keys
                        .Where(id => !_catalog.RouletteExceptions.Contains(id))
                        .OrderBy(x => x)
                        .ToList();

                    var weaponId = choices.Count > 0
                        ? choices[RandomNumberGenerator.GetInt32(choices.Count)]
                        : 1;

                    index = $"weapon-{weaponId}";

                    if (userId != 0)
                    {
                        _economy.RentWeapon(userId, weaponId, now + _catalog.RouletteWeaponRentHours * 3600L);
                    }

                    break;
                }

            case "skill":
                {
                    var skills = _catalog.SkillSp.Keys.OrderBy(x => x).ToList();
                    if (skills.Count == 0)
                    {
                        skills.Add(0);
                    }

                    var skillId = skills[RandomNumberGenerator.GetInt32(skills.Count)];
                    index = $"skill-{skillId}";

                    if (userId != 0)
                    {
                        _economy.RentSkill(userId, skillId, now + _catalog.RouletteSkillRentHours * 3600L);
                    }

                    break;
                }

            case "plusone":
                {
                    index = "plusone";
                    remaining++;
                    _attempts.Set(userId, remaining);
                    break;
                }

            case "blackDivision":
                {
                    index = "blackDivision";
                    var field = _catalog.RouletteBlackDivisionCurrency == 1
                        ? PlayerService.Credits
                        : PlayerService.GamePoints;

                    if (userId != 0)
                    {
                        _players.MutateData(userId, data => PlayerService.Grant(data, field, outcome.Amount));
                    }

                    break;
                }

            case "cr":
            case "gp":
            case "mp":
            case "sp":
                {
                    index = $"{outcome.Kind}-{outcome.Index}";
                    var gain = outcome.Kind == "cr" ? outcome.Amount * level : outcome.Amount;

                    if (userId != 0)
                    {
                        if (outcome.Kind == "mp")
                        {
                            _customization.MutateMastering(userId, mastering =>
                            {
                                mastering["mp"] = Json.ToInt(mastering["mp"]) + gain;
                            });
                        }
                        else
                        {
                            _players.MutateData(userId, data => PlayerService.Grant(data, outcome.Kind, gain));
                        }
                    }

                    break;
                }

            default:
                index = $"{outcome.Kind}-{outcome.Index}";
                break;
        }

        _logger.LogInformation(
            "roulette {Index} (level {Level}) uid{UserId}, {Remaining} attempt(s) left",
            index,
            level,
            userId,
            remaining);

        return Reply.Ok(("index", index));
    }

    public JsonNode Buy(LegacyRequest request)
    {
        var userId = _players.Caller(request) ?? 0;
        var count = request.Int("count", 1);

        if (count < 1)
        {
            return Reply.Fail("Need to ban! Attempt count < 1");
        }

        var cost = _catalog.AttemptGp * count;
        var purchase = _players.Charge(userId, PlayerService.GamePoints, cost);

        if (!purchase.Success)
        {
            return Reply.Fail("Not enough GP");
        }

        var total = _attempts.Get(userId, _catalog.DailyAttempts) + count;
        _attempts.Set(userId, total);

        return Reply.Ok(("attempts", total), ("new_gp", purchase.NewBalance));
    }

    private RouletteOutcome Pick()
    {
        var pool = new List<RouletteOutcome>();

        foreach (var bucket in _catalog.RouletteBonus)
        {
            if (bucket.Key == "hopsKey" || bucket.Value is not JsonArray entries)
            {
                continue;
            }

            for (var i = 0; i < entries.Count; i++)
            {
                var chance = Json.ToDouble(entries[i].Get("chance"));

                if (chance > 0)
                {
                    pool.Add(new RouletteOutcome(bucket.Key, i, Json.ToInt(entries[i].Get("amount")), chance));
                }
            }
        }

        if (pool.Count == 0)
        {
            return new RouletteOutcome("cr", 0, 70, 1);
        }

        var total = pool.Sum(x => x.Chance);
        var roll = RandomNumberGenerator.GetInt32(int.MaxValue) / (double)int.MaxValue * total;
        var accumulated = 0.0;

        foreach (var candidate in pool)
        {
            accumulated += candidate.Chance;

            if (roll <= accumulated)
            {
                return candidate;
            }
        }

        return pool[^1];
    }
}
