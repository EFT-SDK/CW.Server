using CW.Server.Configuration;
using CW.Server.Infrastructure;
using Microsoft.Extensions.Options;
using System.Text.Json.Nodes;

namespace CW.Server.Storage;

public interface ICustomizationRepository
{
    JsonObject Get(int userId);

    JsonObject Mastering(int userId);

    JsonObject MutateMastering(int userId, Action<JsonObject> mutate);

    JsonObject? SaveSuit(int userId, int weaponId, int suitIndex, JsonNode? body);

    void NoteCamo(int userId, int weaponId, int metaLevel, int index);

    void SaveFriends(int userId, JsonNode? data);

    JsonObject SuitsFor(int userId, JsonNode? baseSuits, int? slots = null);

    void UnlockMeta(int userId, int weaponId, string level, IEnumerable<string> slots);

    void UnlockMod(int userId, int weaponId, string level, string slot);

    void UnlockCamo(int userId, int weaponId, int camoId);

    void AddWeaponExp(int userId, int weaponId, int exp);

    void SpendMasteringPoints(int userId, int amount);

    void AddMasteringPoints(int userId, string field, int amount);
}

public sealed class CustomizationRepository : ICustomizationRepository
{
    private readonly ServerPaths _paths;
    private readonly IJsonFileStore _files;
    private readonly StateLock _lock;
    private readonly IClock _clock;
    private readonly ServerOptions _options;

    public CustomizationRepository(
        ServerPaths paths,
        IJsonFileStore files,
        StateLock stateLock,
        IClock clock,
        IOptions<ServerOptions> options)
    {
        _paths = paths;
        _files = files;
        _lock = stateLock;
        _clock = clock;
        _options = options.Value;
    }

    public JsonObject Get(int userId)
    {
        using (_lock.Enter())
        {
            return _files.LoadObject(PathFor(userId));
        }
    }

    public JsonObject Mastering(int userId)
    {
        var stored = Get(userId)["mastering"];
        return stored as JsonObject ?? new JsonObject { ["mp"] = 0, ["weapons"] = new JsonObject() };
    }

    public JsonObject MutateMastering(int userId, Action<JsonObject> mutate)
    {
        using (_lock.Enter())
        {
            var file = _files.LoadObject(PathFor(userId));
            var mastering = file.EnsureObject("mastering");
            mastering.EnsureObject("weapons");

            if (!mastering.ContainsKey("mp"))
            {
                mastering["mp"] = 0;
            }

            mutate(mastering);
            file["changed"] = _clock.UnixSeconds;
            _files.Save(PathFor(userId), file);
            return mastering;
        }
    }

    public JsonObject? SaveSuit(int userId, int weaponId, int suitIndex, JsonNode? body)
    {
        if (body is not JsonObject payload)
        {
            return null;
        }

        var entry = SanitizeEntry(payload);

        using (_lock.Enter())
        {
            var file = _files.LoadObject(PathFor(userId));
            file.EnsureObject("load_suits")
                .EnsureObject(suitIndex.ToString())[weaponId.ToString()] = Json.CloneObject(entry);
            file["changed"] = _clock.UnixSeconds;
            _files.Save(PathFor(userId), file);
        }

        return entry;
    }

    public void NoteCamo(int userId, int weaponId, int metaLevel, int index)
    {
        using (_lock.Enter())
        {
            var file = _files.LoadObject(PathFor(userId));
            var seen = file.EnsureObject("camos_seen").EnsureArray(weaponId.ToString());

            var exists = seen.OfType<JsonArray>().Any(pair =>
                pair.Count == 2 && Json.ToInt(pair[0]) == metaLevel && Json.ToInt(pair[1]) == index);

            if (!exists)
            {
                seen.Add(new JsonArray { metaLevel, index });
            }

            _files.Save(PathFor(userId), file);
        }
    }

    public void SaveFriends(int userId, JsonNode? data)
    {
        using (_lock.Enter())
        {
            var file = _files.LoadObject(PathFor(userId));
            file["friends"] = Json.Clone(data);
            _files.Save(PathFor(userId), file);
        }
    }

    public void UnlockMeta(int userId, int weaponId, string level, IEnumerable<string> slots)
    {
        MutateMastering(userId, mastering =>
        {
            var weapon = WeaponEntry(mastering, weaponId);
            var unlocked = new JsonObject();

            foreach (var slot in slots)
            {
                unlocked[slot] = true;
            }

            weapon.EnsureObject("meta")[level] = unlocked;
        });
    }

    public void UnlockMod(int userId, int weaponId, string level, string slot)
    {
        MutateMastering(userId, mastering =>
        {
            var weapon = WeaponEntry(mastering, weaponId);
            weapon.EnsureObject("meta").EnsureObject(level)[slot] = true;
        });
    }

    public void UnlockCamo(int userId, int weaponId, int camoId)
    {
        MutateMastering(userId, mastering =>
        {
            WeaponEntry(mastering, weaponId).EnsureObject("camo")[camoId.ToString()] = true;
        });
    }

    public void AddWeaponExp(int userId, int weaponId, int exp)
    {
        MutateMastering(userId, mastering =>
        {
            var weapon = WeaponEntry(mastering, weaponId);
            weapon["exp"] = Json.ToInt(weapon["exp"]) + exp;
            weapon["total_exp"] = Json.ToInt(weapon["total_exp"]) + exp;
        });
    }

    public void SpendMasteringPoints(int userId, int amount)
    {
        AddMasteringPoints(userId, "mp_spent", amount);
    }

    public void AddMasteringPoints(int userId, string field, int amount)
    {
        MutateMastering(userId, mastering =>
        {
            mastering[field] = Json.ToInt(mastering[field]) + amount;
        });
    }

    public JsonObject SuitsFor(int userId, JsonNode? baseSuits, int? slots = null)
    {
        var slotCount = slots ?? _options.SuitSlots;
        var template = baseSuits as JsonObject ?? new JsonObject();
        var saved = Get(userId)["load_suits"] as JsonObject ?? new JsonObject();

        var defaults = new JsonObject();
        foreach (var source in new[] { template, saved })
        {
            foreach (var slot in source)
            {
                if (slot.Value is not JsonObject weapons)
                {
                    continue;
                }

                foreach (var weapon in weapons)
                {
                    if (weapon.Value is JsonObject entry && !defaults.ContainsKey(weapon.Key))
                    {
                        defaults[weapon.Key] = SanitizeEntry(entry);
                    }
                }
            }
        }

        var highest = slotCount;
        foreach (var key in template.Select(p => p.Key).Concat(saved.Select(p => p.Key)))
        {
            if (int.TryParse(key, out var index))
            {
                highest = Math.Max(highest, index + 1);
            }
        }

        var result = new JsonObject();

        for (var i = 0; i < highest; i++)
        {
            var target = Json.CloneObject(defaults);

            foreach (var source in new[] { template, saved })
            {
                if (source[i.ToString()] is not JsonObject weapons)
                {
                    continue;
                }

                foreach (var weapon in weapons)
                {
                    if (weapon.Value is JsonObject entry)
                    {
                        target[weapon.Key] = SanitizeEntry(entry);
                    }
                }
            }

            result[i.ToString()] = target;
        }

        return result;
    }

    private static JsonObject WeaponEntry(JsonObject mastering, int weaponId)
    {
        var weapons = mastering.EnsureObject("weapons");
        var key = weaponId.ToString();

        if (weapons[key] is JsonObject existing)
        {
            return existing;
        }

        var created = new JsonObject
        {
            ["exp"] = 0,
            ["total_exp"] = 0,
            ["meta"] = new JsonObject(),
            ["camo"] = new JsonObject(),
        };

        weapons[key] = created;
        return created;
    }

    private static JsonObject SanitizeEntry(JsonObject entry)
    {
        var result = new JsonObject();

        foreach (var pair in entry)
        {
            if (TryReadInt(pair.Value, out var value))
            {
                result[pair.Key] = value;
            }
        }

        return result;
    }

    private static bool TryReadInt(JsonNode? node, out int value)
    {
        value = 0;

        if (node is not JsonValue jsonValue)
        {
            return false;
        }

        if (jsonValue.TryGetValue<int>(out value))
        {
            return true;
        }

        if (jsonValue.TryGetValue<long>(out var asLong))
        {
            value = unchecked((int)asLong);
            return true;
        }

        if (jsonValue.TryGetValue<double>(out var asDouble) && double.IsFinite(asDouble))
        {
            value = (int)asDouble;
            return true;
        }

        if (jsonValue.TryGetValue<string>(out var text) && Json.IsIntegerText(text))
        {
            value = Json.ParseInt(text);
            return true;
        }

        return false;
    }

    private string PathFor(int userId) => Path.Combine(_paths.State, "customization", $"{userId}.json");
}
