using CW.Server.Configuration;
using CW.Server.Data;
using CW.Server.Infrastructure;
using Microsoft.Extensions.Options;
using System.Text.Json.Nodes;

namespace CW.Server.Services;

public sealed class ProfileFactory
{
    private const int UndestructableSentinel = -77;

    private readonly IGameDataProvider _data;
    private readonly GameCatalog _catalog;
    private readonly ServerOptions _options;

    public ProfileFactory(IGameDataProvider data, GameCatalog catalog, IOptions<ServerOptions> options)
    {
        _data = data;
        _catalog = catalog;
        _options = options.Value;
    }

    public JsonObject BuildFresh(int userId, string nick)
    {
        var template = _data.Template("fresh_load");

        if (template.Count > 0)
        {
            var profile = Json.CloneObject(template);
            var data = profile.EnsureObject("data");
            data["username"] = nick;
            data["userID"] = userId;
            profile["result"] = 0;
            profile["newUser"] = true;
            profile.EnsureObject("stats");
            profile.EnsureObject("social");
            profile["hops_secret_key"] = new string('0', 32);
            return profile;
        }

        return DeriveFreshFromMax(userId, nick);
    }

    public JsonObject BuildMax(int userId, string nick)
    {
        var template = _data.Template("load");

        if (template.Count == 0)
        {
            throw new InvalidOperationException("templates/load.json missing from CW.Server_Data");
        }

        var profile = Json.CloneObject(template);
        var data = profile.EnsureObject("data");

        if (!_options.MaxProfile)
        {
            data["username"] = nick;
            profile["result"] = 0;
            return profile;
        }

        data["username"] = nick;
        data["userID"] = userId;
        data["nickname_color"] = "#FFFFFF";
        data["permission"] = 3;
        data["banned"] = 0;
        data["is_suspect"] = false;
        data["currentXP"] = _catalog.MaxLevelXp;
        data["currentLevel"] = 70;
        data["cr"] = 999999999;
        data["gp"] = 999999999;
        data["sp"] = 9999;
        data["bg"] = 999999;
        data["sp_available"] = 9999;
        data["votes"] = 999;
        data["repa"] = 999;
        data["renameCount"] = 999;
        data["newLevel"] = -1;

        if (data["awards"] is not JsonArray)
        {
            data["awards"] = new JsonArray();
        }

        foreach (var weapon in data.Arr("weapons").OfType<JsonObject>())
        {
            weapon["unlocked"] = true;
            weapon["repair_info"] = UndestructableSentinel;
            weapon["wtaskCurrent"] = Json.ToInt(weapon["wtaskMax"], 150);
        }

        foreach (var skill in data.Arr("skills").OfType<JsonObject>())
        {
            skill["unlocked"] = true;
        }

        var achievements = data.Arr("achievements");
        var maxedAchievements = new JsonArray();
        for (var i = 0; i < achievements.Count; i++)
        {
            maxedAchievements.Add(9999);
        }

        data["achievements"] = maxedAchievements;
        data["unlockedSets"] = Filled(1, Math.Max(6, data.Arr("unlockedSets").Count));

        for (var i = 0; i < 5; i++)
        {
            if (data[$"info{i}"] is JsonObject info)
            {
                info["unlocked"] = true;
            }
        }

        profile["result"] = 0;
        profile["newUser"] = false;
        profile.EnsureObject("stats");
        profile.EnsureObject("social");
        profile["hops_secret_key"] = new string('0', 32);
        return profile;
    }

    public static JsonObject ApplyUnlockAll(JsonObject profile)
    {
        if (profile["data"] is not JsonObject data)
        {
            return profile;
        }

        foreach (var weapon in data.Arr("weapons").OfType<JsonObject>())
        {
            weapon["unlocked"] = true;
            weapon["repair_info"] = UndestructableSentinel;

            if (Json.ToInt(weapon["wtaskCurrent"]) == 0)
            {
                weapon["wtaskCurrent"] = UndestructableSentinel;
            }
        }

        foreach (var skill in data.Arr("skills").OfType<JsonObject>())
        {
            skill["unlocked"] = true;
        }

        data["unlockedSets"] = data["unlockedSets"] is JsonArray sets
            ? Filled(1, sets.Count)
            : Filled(1, 6);

        for (var i = 0; i < 5; i++)
        {
            if (data[$"info{i}"] is JsonObject info)
            {
                info["unlocked"] = true;
            }
        }

        return profile;
    }

    public JsonObject BuildFreshModStates()
    {
        var captured = _data.Template("fresh_mod_states");
        if (captured.Count > 0)
        {
            return Json.CloneObject(captured);
        }

        var states = BuildModStates();
        var result = new JsonObject();

        foreach (var pair in states)
        {
            result[pair.Key] = new JsonObject
            {
                ["exp"] = 0,
                ["total_exp"] = 0,
                ["meta"] = new JsonObject
                {
                    ["-1"] = new JsonObject
                    {
                        ["1"] = true,
                        ["2"] = true,
                        ["3"] = true,
                        ["4"] = true,
                    },
                },
                ["camo"] = new JsonObject { ["60"] = true },
            };
        }

        return result;
    }

    public JsonObject BuildModStates()
    {
        var weaponInfo = _data.Template("customization_main_load").Obj("weapon_info").Obj("data");

        var camo = new JsonObject();
        foreach (var camoId in _catalog.CamoIds)
        {
            camo[camoId.ToString()] = true;
        }

        var states = new JsonObject();

        foreach (var weapon in weaponInfo)
        {
            var metaSource = weapon.Value.Obj("meta");
            var meta = new JsonObject();

            foreach (var level in metaSource)
            {
                var slots = level.Value.Obj("mods");
                var unlocked = new JsonObject();

                if (slots.Count > 0)
                {
                    foreach (var slot in slots)
                    {
                        unlocked[slot.Key] = true;
                    }
                }
                else
                {
                    for (var i = 1; i <= 5; i++)
                    {
                        unlocked[i.ToString()] = true;
                    }
                }

                meta[level.Key] = unlocked;
            }

            if (!meta.ContainsKey("-1"))
            {
                var baseLevel = new JsonObject();
                for (var i = 1; i <= 5; i++)
                {
                    baseLevel[i.ToString()] = true;
                }

                meta["-1"] = baseLevel;
            }

            states[weapon.Key] = new JsonObject
            {
                ["exp"] = 999999,
                ["total_exp"] = 999999,
                ["camo"] = Json.CloneObject(camo),
                ["meta"] = meta,
            };
        }

        return states;
    }

    private JsonObject DeriveFreshFromMax(int userId, string nick)
    {
        var profile = BuildMax(userId, nick);
        var data = profile.EnsureObject("data");

        data["currentXP"] = 0;
        data["currentLevel"] = 0;
        data["cr"] = 10000;
        data["gp"] = 100;
        data["sp"] = 5;
        data["bg"] = 0;
        data["sp_available"] = 10;
        data["votes"] = 3;
        data["repa"] = 0;
        data["renameCount"] = 2;
        data["permission"] = 0;

        var weapons = data.Arr("weapons");
        for (var i = 0; i < weapons.Count; i++)
        {
            if (weapons[i] is not JsonObject weapon)
            {
                continue;
            }

            var unlocked = i is 0 or 6;
            weapon["unlocked"] = unlocked;
            weapon["repair_info"] = unlocked ? UndestructableSentinel : 0;
            weapon["wtaskCurrent"] = 0;
        }

        foreach (var skill in data.Arr("skills").OfType<JsonObject>())
        {
            skill["unlocked"] = false;
        }

        data["achievements"] = Filled(0, data.Arr("achievements").Count);

        var setCount = Math.Max(6, data.Arr("unlockedSets").Count);
        var sets = new JsonArray { 1 };
        for (var i = 1; i < setCount; i++)
        {
            sets.Add(0);
        }

        data["unlockedSets"] = sets;
        profile["newUser"] = true;
        return profile;
    }

    private static JsonArray Filled(int value, int count)
    {
        var array = new JsonArray();
        for (var i = 0; i < count; i++)
        {
            array.Add(value);
        }

        return array;
    }
}
