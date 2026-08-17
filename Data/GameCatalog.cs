using CW.Server.Infrastructure;
using System.Text.Json.Nodes;

namespace CW.Server.Data;

public sealed record MasteringModLocation(string Level, string Slot, int MasteringPoints);

public sealed record CamoPrice(int Currency, int Price);

public sealed class GameCatalog
{
    private readonly IGameDataProvider _data;
    private readonly Dictionary<int, CamoPrice> _camoPrices;

    public GameCatalog(IGameDataProvider data)
    {
        _data = data;
        var globals = data.Backend("getGlobals");

        KitGp = IntList(globals["weaponSetPrices"], new[] { 0, 5, 25, 50, 50 });
        SetGp = IntList(globals["unlockSetPrices"], new[] { 0, 100, 300, 600, 1000, 1500 });
        SpGp = Json.ToInt(globals["buySPPrice"], 70);
        NickGp = Json.ToInt(globals["buyNicknameChangePrice"], 100);
        NickColorGp = Json.ToInt(globals["buyNickColorChangePrice"], 300);
        MpGp = Json.ToInt(globals["gpForMp"], 10);
        ContractsTimerHours = Math.Max(1, Json.ToInt(globals["contractsTimer"], 24));
        MasteringExpPerPoint = Json.ToInt(globals["expForMp"], 5000);
        SkillResetCr = Json.ToInt(globals["skillsResetPriceCr"], 5000);

        ClanPrice = new Dictionary<int, int>
        {
            [0] = Json.ToInt(globals["clanCRPrice"]),
            [1] = Json.ToInt(globals["clanBasePrice"], 500),
            [2] = Json.ToInt(globals["clanExtendedPrice"], 1250),
            [3] = Json.ToInt(globals["clanPremiumPrice"], 2500),
        };

        ClanCurrency = new Dictionary<int, string> { [0] = "cr", [1] = "gp", [2] = "gp", [3] = "gp" };
        ClanMembers = new Dictionary<int, int> { [0] = 10, [1] = 10, [2] = 20, [3] = 50 };

        ClanExtendGp = Json.ToInt(globals["clanExtendPrice"], 500);
        ClanAssignGp = Json.ToInt(globals["clanAssignmentCosts"], 50);
        ClanLinkGp = Json.ToInt(globals["clanChangeUrlCost"], 50);
        ClanColorGp = Json.ToInt(globals["clanChangeTagColorCost"], 50);
        ClanExpTable = IntList(globals["clanExpTable"], new[] { 0 });
        ExpTable = IntList(globals["expTable"], new[] { 0 });
        MaxLevelXp = ExpTable[Math.Min(70, ExpTable.Count - 1)];

        var roulette = Json.CloneObject(globals["roulette"]);
        var standalone = data.Backend("getRoulette");
        if (standalone["bonus"] is not null)
        {
            foreach (var pair in standalone)
            {
                roulette[pair.Key] = Json.Clone(pair.Value);
            }
        }

        AttemptGp = Json.ToInt(roulette["attemptCost"], 15);
        DailyAttempts = Json.ToInt(roulette["dailyCount"], 3);
        RouletteBonus = roulette.Obj("bonus");
        RouletteExceptions = roulette.Arr("weaponExceptions").Select(x => Json.ToInt(x)).ToHashSet();
        RouletteWeaponRentHours = Json.ToInt(roulette.Obj("rent")["weapon"], 6);
        RouletteSkillRentHours = Json.ToInt(roulette.Obj("rent")["skill"], 6);
        RouletteBlackDivisionCurrency = Json.ToInt(roulette["blackDivisionCurrency"], 1);

        var weaponCr = new Dictionary<int, int>();
        var weaponRentGp = new Dictionary<int, IReadOnlyList<int>>();
        var weaponRentDays = new Dictionary<int, IReadOnlyList<int>>();
        var weaponPermanentGp = new Dictionary<int, int>();
        var weaponWtaskGp = new Dictionary<int, int>();
        var weaponPremium = new Dictionary<int, bool>();

        var weapons = data.Backend("getWeaponsEn").Arr("weapons");
        for (var i = 0; i < weapons.Count; i++)
        {
            var weapon = weapons[i];
            weaponCr[i] = Json.ToInt(weapon.Get("price"));

            if (weapon.Get("rentPrice") is JsonArray rentPrice && rentPrice.Count > 0)
            {
                weaponRentGp[i] = rentPrice.Select(x => Json.ToInt(x)).ToList();
            }

            if (weapon.Get("rentTime") is JsonArray rentTime && rentTime.Count > 0)
            {
                weaponRentDays[i] = rentTime.Select(x => Json.ToInt(x)).ToList();
            }

            weaponPermanentGp[i] = Json.ToInt(weapon.Get("permanentPrice"));
            weaponWtaskGp[i] = weapon.Has("wtaskPrice") ? Json.ToInt(weapon.Get("wtaskPrice"), 1000) : 1000;
            weaponPremium[i] = Json.ToBool(weapon.Get("isPremium"));
        }

        WeaponCr = weaponCr;
        WeaponRentGp = weaponRentGp;
        WeaponRentDays = weaponRentDays;
        WeaponPermanentGp = weaponPermanentGp;
        WeaponWtaskGp = weaponWtaskGp;
        WeaponPremium = weaponPremium;

        var skillSp = new Dictionary<int, int>();
        Skills = data.Backend("getSkillsEn").Arr("skills");
        for (var i = 0; i < Skills.Count; i++)
        {
            skillSp[i] = Json.ToInt(Skills[i].Get("SP"), 1);
        }

        SkillSp = skillSp;
        ClanSkills = data.Backend("getClanSkillsEn").Arr("clan_skills");

        var boxGp = new Dictionary<int, int>();
        var boxCr = new Dictionary<int, int>();
        var boxItems = new Dictionary<int, JsonArray>();

        var boxSource = data.Backend("getBoxesEn");
        var boxes = boxSource["packages"] as JsonArray
                    ?? boxSource["boxes"] as JsonArray
                    ?? boxSource["data"] as JsonArray
                    ?? new JsonArray();

        for (var i = 0; i < boxes.Count; i++)
        {
            boxGp[i] = Json.ToInt(boxes[i].Get("price_gp"));
            boxCr[i] = Json.ToInt(boxes[i].Get("price_cr"));
            boxItems[i] = Json.CloneArray(boxes[i].Get("items"));
        }

        BoxGp = boxGp;
        BoxCr = boxCr;
        BoxItems = boxItems;

        _camoPrices = new Dictionary<int, CamoPrice>();
        CamoPrices = _camoPrices;
        CamoIds = new SortedSet<int>();
        WalkMods(data.Backend("getWeaponMods"));

        if (CamoIds.Count == 0)
        {
            for (var i = 1; i < 200; i++)
            {
                CamoIds.Add(i);
            }
        }

        (MetaSlots, MetaGp, ModIndex) = BuildMasteringLayout();
    }

    public IReadOnlyList<int> KitGp { get; }

    public IReadOnlyList<int> SetGp { get; }

    public int SpGp { get; }

    public int NickGp { get; }

    public int NickColorGp { get; }

    public int MpGp { get; }

    public int ContractsTimerHours { get; }

    public int ContractsPeriodSeconds => ContractsTimerHours * 3600;

    public int MasteringExpPerPoint { get; }

    public int SkillResetCr { get; }

    public IReadOnlyDictionary<int, int> ClanPrice { get; }

    public IReadOnlyDictionary<int, string> ClanCurrency { get; }

    public IReadOnlyDictionary<int, int> ClanMembers { get; }

    public int ClanExtendGp { get; }

    public int ClanAssignGp { get; }

    public int ClanLinkGp { get; }

    public int ClanColorGp { get; }

    public IReadOnlyList<int> ClanExpTable { get; }

    public IReadOnlyList<int> ExpTable { get; }

    public int MaxLevelXp { get; }

    public int AttemptGp { get; }

    public int DailyAttempts { get; }

    public JsonObject RouletteBonus { get; }

    public IReadOnlySet<int> RouletteExceptions { get; }

    public int RouletteWeaponRentHours { get; }

    public int RouletteSkillRentHours { get; }

    public int RouletteBlackDivisionCurrency { get; }

    public IReadOnlyDictionary<int, int> WeaponCr { get; }

    public IReadOnlyDictionary<int, IReadOnlyList<int>> WeaponRentGp { get; }

    public IReadOnlyDictionary<int, IReadOnlyList<int>> WeaponRentDays { get; }

    public IReadOnlyDictionary<int, int> WeaponPermanentGp { get; }

    public IReadOnlyDictionary<int, int> WeaponWtaskGp { get; }

    public IReadOnlyDictionary<int, bool> WeaponPremium { get; }

    public IReadOnlyDictionary<int, int> SkillSp { get; }

    public JsonArray Skills { get; }

    public JsonArray ClanSkills { get; }

    public IReadOnlyDictionary<int, int> BoxGp { get; }

    public IReadOnlyDictionary<int, int> BoxCr { get; }

    public IReadOnlyDictionary<int, JsonArray> BoxItems { get; }

    public IReadOnlyDictionary<int, CamoPrice> CamoPrices { get; }

    public SortedSet<int> CamoIds { get; }

    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>> MetaSlots { get; }

    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> MetaGp { get; }

    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, MasteringModLocation>> ModIndex { get; }

    public int LevelForXp(int xp) => LevelFromTable(xp, ExpTable);

    public static int LevelFromTable(int value, IReadOnlyList<int> table)
    {
        if (table.Count == 0)
        {
            return 0;
        }

        var level = 0;
        for (var i = 0; i < table.Count; i++)
        {
            if (value >= table[i])
            {
                level = i;
            }
            else
            {
                break;
            }
        }

        return level;
    }

    public static int MasteringPointGrant(int level) => 20 + level * 5;

    private (
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>>,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>>,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, MasteringModLocation>>)
        BuildMasteringLayout()
    {
        var slots = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>>();
        var gp = new Dictionary<string, IReadOnlyDictionary<string, int>>();
        var index = new Dictionary<string, IReadOnlyDictionary<string, MasteringModLocation>>();

        var weaponInfo = _data.Template("customization_main_load").Obj("weapon_info").Obj("data");

        foreach (var weapon in weaponInfo)
        {
            var weaponId = weapon.Key;
            var meta = weapon.Value.Obj("meta");

            var levelSlots = new Dictionary<string, IReadOnlyList<string>>();
            var levelGp = new Dictionary<string, int>();
            var modLookup = new Dictionary<string, MasteringModLocation>();

            foreach (var level in meta)
            {
                var mods = level.Value.Obj("mods");
                levelSlots[level.Key] = mods.Select(m => m.Key).ToList();
                levelGp[level.Key] = Json.ToInt(level.Value.Get("gp"));

                foreach (var slot in mods)
                {
                    var modId = slot.Value.Get("mod");
                    if (modId is not null)
                    {
                        modLookup[Json.ToText(modId)] = new MasteringModLocation(
                            level.Key,
                            slot.Key,
                            Json.ToInt(slot.Value.Get("mp"), 1));
                    }
                }
            }

            slots[weaponId] = levelSlots;
            gp[weaponId] = levelGp;
            index[weaponId] = modLookup;
        }

        return (slots, gp, index);
    }

    private void WalkMods(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                if (Json.ToText(obj["type"]) == "3" && obj.ContainsKey("id"))
                {
                    var camoId = Json.ToInt(obj["id"], -1);
                    if (camoId >= 0)
                    {
                        CamoIds.Add(camoId);
                        _camoPrices[camoId] = new CamoPrice(
                            Json.ToInt(obj["currency_for_character"], 1),
                            Json.ToInt(obj["price_for_character"]));
                    }
                }

                foreach (var pair in obj)
                {
                    WalkMods(pair.Value);
                }

                break;

            case JsonArray array:
                foreach (var item in array)
                {
                    WalkMods(item);
                }

                break;
        }
    }

    private static List<int> IntList(JsonNode? node, IEnumerable<int> fallback)
    {
        if (node is JsonArray array && array.Count > 0)
        {
            return array.Select(x => Json.ToInt(x)).ToList();
        }

        return fallback.ToList();
    }
}
