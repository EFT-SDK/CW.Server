using CW.Server.Data;
using CW.Server.Infrastructure;
using CW.Server.Services;
using CW.Server.Storage;
using CW.Server.Transport;
using System.Text.Json.Nodes;

namespace CW.Server.Endpoints;

public sealed class MasteringEndpoints
{
    private const int CurrencyGamePoints = 2;

    private readonly IGameDataProvider _data;
    private readonly ICustomizationRepository _customization;
    private readonly MasteringService _mastering;
    private readonly PlayerService _players;
    private readonly GameCatalog _catalog;
    private readonly ITransactionLedger _ledger;
    private readonly ILogger<MasteringEndpoints> _logger;

    public MasteringEndpoints(
        IGameDataProvider data,
        ICustomizationRepository customization,
        MasteringService mastering,
        PlayerService players,
        GameCatalog catalog,
        ITransactionLedger ledger,
        ILogger<MasteringEndpoints> logger)
    {
        _data = data;
        _customization = customization;
        _mastering = mastering;
        _players = players;
        _catalog = catalog;
        _ledger = ledger;
        _logger = logger;
    }

    public JsonNode MainLoad(LegacyRequest request)
    {
        var userId = _players.Caller(request) ?? 0;
        var payload = _data.TemplateCopy("customization_main_load");

        payload["load"] = _mastering.Load(userId);
        payload["load_suits"] = _customization.SuitsFor(userId, payload["load_suits"]);

        return payload;
    }

    public JsonNode LoadSuits(LegacyRequest request)
    {
        var userId = _players.Caller(request) ?? 0;
        var template = _data.Template("customization_main_load")["load_suits"];

        return _customization.SuitsFor(userId, template);
    }

    public JsonNode PlayerLoad(LegacyRequest request)
    {
        return _mastering.Load(_players.Caller(request) ?? 0);
    }

    public JsonNode ServerLoad(LegacyRequest request, int? explicitUserId)
    {
        var userId = explicitUserId ?? _players.Caller(request) ?? 0;
        return _mastering.Load(userId);
    }

    public JsonNode Save(LegacyRequest request)
    {
        if (request.Body is not JsonObject payload)
        {
            return Reply.Ok();
        }

        foreach (var account in payload)
        {
            if (account.Value is not JsonObject stats || !int.TryParse(account.Key, out var userId) || userId <= 0)
            {
                continue;
            }

            var touched = new List<string>();

            foreach (var stat in stats)
            {
                var gain = Json.ToInt(stat.Value);
                if (gain <= 0)
                {
                    continue;
                }

                if (stat.Key == "general_exp")
                {
                    _mastering.GrantMasteringExp(userId, gain);
                    touched.Add($"general_exp+{gain}");
                    continue;
                }

                if (int.TryParse(stat.Key, out var weaponId))
                {
                    _customization.AddWeaponExp(userId, weaponId, gain);
                    touched.Add($"{weaponId}+{gain}");
                }
            }

            if (touched.Count > 0)
            {
                _logger.LogInformation("mastering saved uid{UserId}: {Stats}", userId, string.Join(", ", touched));
            }
        }

        return Reply.Ok();
    }

    public JsonNode SaveSuits(LegacyRequest request, int weaponId, int suitIndex)
    {
        var userId = _players.Caller(request) ?? 0;
        var entry = _customization.SaveSuit(userId, weaponId, suitIndex, request.Body);

        if (entry is null)
        {
            _logger.LogWarning("save_suits w{WeaponId} suit{SuitIndex}: unreadable body", weaponId, suitIndex);
            return Reply.Ok();
        }

        if (entry["camo"] is not null)
        {
            _customization.UnlockCamo(userId, weaponId, Json.ToInt(entry["camo"]));
        }

        _logger.LogInformation(
            "saved w{WeaponId} suit{SuitIndex} uid{UserId}: {Count} mod(s) camo={Camo}",
            weaponId,
            suitIndex,
            userId,
            entry.Count(p => p.Key != "camo"),
            Json.ToText(entry["camo"], "none"));

        return Reply.Ok();
    }

    public JsonNode SetCamoInfo(LegacyRequest request, int weaponId, int metaLevel, int index)
    {
        var userId = _players.Caller(request) ?? 0;
        _customization.NoteCamo(userId, weaponId, metaLevel, index);
        return Reply.Ok();
    }

    public JsonNode BuyMeta(LegacyRequest request, int weaponId, int metaLevel)
    {
        var userId = _players.Caller(request) ?? 0;
        var level = metaLevel.ToString();
        var slots = _mastering.MetaSlots(weaponId, level);
        var cost = _mastering.MetaCost(weaponId, level);

        if (userId != 0 && cost != 0)
        {
            _players.MutateData(userId, data =>
            {
                data["gp"] = Math.Max(0, Json.ToInt(data["gp"]) - cost);
            });
        }

        _customization.UnlockMeta(userId, weaponId, level, slots);

        _logger.LogInformation(
            "meta {Level} unlocked on w{WeaponId} uid{UserId} (-{Cost}gp)",
            metaLevel,
            weaponId,
            userId,
            cost);

        return WeaponStatsEnvelope(userId, weaponId);
    }

    public JsonNode BuyMod(LegacyRequest request, int weaponId, int modId)
    {
        var userId = _players.Caller(request) ?? 0;
        var location = _mastering.LocateMod(weaponId, modId);

        if (location is not null)
        {
            _customization.UnlockMod(userId, weaponId, location.Level, location.Slot);

            if (userId != 0 && location.MasteringPoints != 0)
            {
                _customization.SpendMasteringPoints(userId, location.MasteringPoints);
            }

            _logger.LogInformation(
                "mod {ModId} (meta {Level}/slot {Slot}, -{Points}mp) unlocked on w{WeaponId} uid{UserId}",
                modId,
                location.Level,
                location.Slot,
                location.MasteringPoints,
                weaponId,
                userId);
        }
        else
        {
            _logger.LogInformation("mod {ModId} on w{WeaponId}: no mapping, acknowledged", modId, weaponId);
        }

        return WeaponStatsEnvelope(userId, weaponId);
    }

    public JsonNode UnlockWeaponTaskMeta(LegacyRequest request, int weaponId)
    {
        var userId = _players.Caller(request) ?? 0;
        var slots = _mastering.MetaSlots(weaponId, "0");

        _customization.UnlockMeta(userId, weaponId, "0", slots);
        _logger.LogInformation("wtask meta unlocked on w{WeaponId} uid{UserId}", weaponId, userId);

        return WeaponStatsEnvelope(userId, weaponId);
    }

    public JsonNode SetWeaponTaskInfo(LegacyRequest request, int weaponId)
    {
        return Reply.Ok();
    }

    public JsonNode BuyMasteringPoints(LegacyRequest request, int count)
    {
        var userId = _players.Caller(request);
        var amount = Math.Max(1, count);
        var price = _catalog.MpGp * amount;

        if (userId is null)
        {
            return Reply.Ok();
        }

        if (Json.ToInt(_players.Data(userId.Value)["gp"]) < price)
        {
            return Reply.Fail("failed");
        }

        _players.MutateData(userId.Value, data =>
        {
            data["gp"] = Json.ToInt(data["gp"]) - price;
        });

        _customization.AddMasteringPoints(userId.Value, "mp_bought", amount);
        _ledger.Record(userId, CurrencyGamePoints, -price, $"+ {amount} MP");

        return Reply.Ok();
    }

    private JsonObject WeaponStatsEnvelope(int userId, int weaponId)
    {
        return new JsonObject
        {
            [weaponId.ToString()] = _mastering.WeaponStats(userId, weaponId),
        };
    }
}
