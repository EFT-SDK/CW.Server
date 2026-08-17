using CW.Server.Data;
using CW.Server.Infrastructure;
using System.Text.Json.Nodes;

namespace CW.Server.Services;

public sealed class ProfileNormalizer
{
    private static readonly string[] WeaponKeepFields = { "id", "market_amount", "wtaskMax", "wtaskSelected" };

    private static readonly string[] PodgonFields =
    {
        "result", "weapon_id", "rent_time",
        "new_cr", "win_cr", "new_sp", "win_sp",
        "new_gp", "win_gp", "new_bg", "win_bg",
    };

    private readonly IGameDataProvider _data;
    private readonly GameCatalog _catalog;
    private readonly IClock _clock;
    private JsonArray? _weaponTemplate;

    public ProfileNormalizer(IGameDataProvider data, GameCatalog catalog, IClock clock)
    {
        _data = data;
        _catalog = catalog;
        _clock = clock;
    }

    public static JsonObject PodgonNone()
    {
        var podgon = new JsonObject { ["result"] = 0 };
        foreach (var field in PodgonFields)
        {
            if (field != "result")
            {
                podgon[field] = -1;
            }
        }

        return podgon;
    }

    public bool Normalize(JsonObject profile)
    {
        if (profile["data"] is not JsonObject data)
        {
            return false;
        }

        var changed = NormalizeContracts(data);
        changed |= NormalizeWeapons(data);
        changed |= NormalizePodgon(data);
        return changed;
    }

    private bool NormalizePodgon(JsonObject data)
    {
        var fixedPodgon = PodgonNone();

        if (data["podgon"] is JsonObject current)
        {
            foreach (var pair in current)
            {
                if (fixedPodgon.ContainsKey(pair.Key) && IsPlainInteger(pair.Value))
                {
                    fixedPodgon[pair.Key] = Json.ToInt(pair.Value);
                }
            }

            if (Json.Write(current) == Json.Write(fixedPodgon))
            {
                return false;
            }
        }

        data["podgon"] = fixedPodgon;
        return true;
    }

    private bool NormalizeWeapons(JsonObject data)
    {
        if (data["weapons"] is not JsonArray weapons)
        {
            return false;
        }

        var template = WeaponTemplate();
        var changed = false;

        for (var i = 0; i < weapons.Count; i++)
        {
            if (weapons[i] is not JsonObject weapon)
            {
                continue;
            }

            var source = i < template.Count ? template[i] as JsonObject : null;

            if (!weapon.ContainsKey("id"))
            {
                weapon["id"] = Json.ToInt(source?["id"], i);
                changed = true;
            }

            foreach (var key in WeaponKeepFields)
            {
                if (key == "id" || weapon.ContainsKey(key))
                {
                    continue;
                }

                if (source is not null && source.TryGetPropertyValue(key, out var value))
                {
                    weapon[key] = Json.Clone(value);
                    changed = true;
                }
            }
        }

        return changed;
    }

    private bool NormalizeContracts(JsonObject data)
    {
        var changed = false;

        if (data["contracts"] is not JsonObject contracts)
        {
            contracts = new JsonObject
            {
                ["user_id"] = Json.ToText(data["social_id"], "0"),
                ["easy_counter"] = 0,
                ["normal_counter"] = 0,
                ["hard_counter"] = 0,
                ["current_easy"] = 0,
                ["current_normal"] = 0,
                ["current_hard"] = 0,
                ["timer_end"] = 0,
            };

            data["contracts"] = contracts;
            changed = true;
        }

        if (!IsPlainInteger(contracts["timer_end"]))
        {
            changed = true;
        }

        var end = Json.ToLong(contracts["timer_end"]);
        var now = _clock.UnixSeconds;

        if (end <= now)
        {
            contracts["current_easy"] = 0;
            contracts["current_normal"] = 0;
            contracts["current_hard"] = 0;
            end = now + _catalog.ContractsPeriodSeconds;
            changed = true;
        }

        contracts["timer_end"] = end;
        return changed;
    }

    private JsonArray WeaponTemplate()
    {
        return _weaponTemplate ??= _data.Template("fresh_load").Obj("data").Arr("weapons");
    }

    private static bool IsPlainInteger(JsonNode? node)
    {
        return node is JsonValue value
               && !value.TryGetValue<bool>(out _)
               && (value.TryGetValue<int>(out _) || value.TryGetValue<long>(out _));
    }
}
