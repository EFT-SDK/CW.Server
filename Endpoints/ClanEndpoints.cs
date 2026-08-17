using CW.Server.Configuration;
using CW.Server.Data;
using CW.Server.Infrastructure;
using CW.Server.Services;
using CW.Server.Storage;
using CW.Server.Transport;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Text.Json.Nodes;

namespace CW.Server.Endpoints;

public sealed class ClanEndpoints
{
    private const int CurrencyCredits = 1;
    private const int CurrencyGamePoints = 2;
    private const int MaxClanType = 3;

    private readonly IClanRepository _clans;
    private readonly IProfileRepository _profiles;
    private readonly PlayerService _players;
    private readonly EconomyEndpoints _economy;
    private readonly GameCatalog _catalog;
    private readonly ITransactionLedger _ledger;
    private readonly ServerOptions _options;
    private readonly IClock _clock;
    private readonly ILogger<ClanEndpoints> _logger;

    public ClanEndpoints(
        IClanRepository clans,
        IProfileRepository profiles,
        PlayerService players,
        EconomyEndpoints economy,
        GameCatalog catalog,
        ITransactionLedger ledger,
        IOptions<ServerOptions> options,
        IClock clock,
        ILogger<ClanEndpoints> logger)
    {
        _clans = clans;
        _profiles = profiles;
        _players = players;
        _economy = economy;
        _catalog = catalog;
        _ledger = ledger;
        _options = options.Value;
        _clock = clock;
        _logger = logger;
    }

    public JsonNode Route(LegacyRequest request)
    {
        var action = request.Text("action").ToLowerInvariant();
        var me = _players.CallerIgnoringQuery(request);
        var clanArg = request.Int("clan_id") is var raw && raw != 0 ? raw.ToString() : string.Empty;
        var target = request.Int("user_id");

        return action switch
        {
            "list_clans" => ListClans(request, me),
            "check" => Check(request),
            "clan_info" => ClanInfo(clanArg, me),
            "create_clan" => CreateClan(request, me),
            "upgrade_clan" => UpgradeClan(clanArg, me),
            "send_request" => SendRequest(clanArg, me),
            "revoke_request" or "delete_request" => RemoveRequest(clanArg, target != 0 ? target : me),
            "delete_all_requests" => DeleteAllRequests(clanArg),
            "accept_request" => AcceptRequest(clanArg, target),
            "kick_from_clan" or "exit_from_clan" => Kick(clanArg, target != 0 ? target : me),
            "assign_officer" or "assign_alt_leader" or "dismiss_officer"
                or "dismiss_alt_leader" or "change_leader" => AssignRole(action, me, target),
            "set_color" or "set_link" or "post_message" or "save_ratio" => SetField(request, action, me),
            "buyskill" or "rentskill" => BuySkill(request, action, me),
            "transfer" => Transfer(request, me),
            "get_transactions" => ClanTransactions(clanArg, me),
            _ => Reply.Ok(),
        };
    }

    public JsonNode ResetClanRequests(LegacyRequest request)
    {
        var me = _players.CallerIgnoringQuery(request) ?? 0;

        _clans.Mutate(data =>
        {
            foreach (var pair in data)
            {
                if (pair.Value is JsonObject clan)
                {
                    clan["requests"] = Without(clan.Arr("requests"), me);
                }
            }
        });

        return Reply.Ok(("msg", "success"));
    }

    public JsonNode GetClanRating(LegacyRequest request)
    {
        var rows = new JsonArray();
        var order = 1;

        var ordered = _clans.All()
            .Where(p => p.Value is JsonObject)
            .OrderByDescending(p => Json.ToInt(p.Value!["clan_exp"]));

        foreach (var pair in ordered)
        {
            rows.Add(ClanRow(pair.Key, (JsonObject)pair.Value!, order, false));
            order++;
        }

        return new JsonObject
        {
            ["result"] = 0,
            ["data"] = Json.CloneArray(rows),
            ["list"] = rows,
        };
    }

    private JsonNode ListClans(LegacyRequest request, int? me)
    {
        var all = _clans.All();
        var top = request.Contains("topType");
        var term = request.Text("searchTerm").Trim().ToLowerInvariant();

        var items = all
            .Where(p => p.Value is JsonObject)
            .Select(p => (Id: p.Key, Clan: (JsonObject)p.Value!))
            .ToList();

        if (term.Length > 0)
        {
            items = items
                .Where(x => Json.ToText(x.Clan["name"]).ToLowerInvariant().Contains(term)
                            || Json.ToText(x.Clan["tag"]).ToLowerInvariant().Contains(term))
                .ToList();
        }

        var sortKey = top ? "race_exp" : "clan_exp";
        items = items.OrderByDescending(x => Json.ToInt(x.Clan[sortKey])).ToList();

        if (top)
        {
            items = items.Take(_options.ClanTopSize).ToList();
        }

        var rows = new JsonArray();
        var order = 1;

        foreach (var item in items)
        {
            var row = ClanRow(item.Id, item.Clan, order, top);
            row["did_request"] = item.Clan.Arr("requests").Any(x => Json.ToInt(x) == (me ?? 0));
            rows.Add(row);
            order++;
        }

        return new JsonObject
        {
            ["result"] = 0,
            ["list"] = rows,
            ["race_info"] = new JsonObject
            {
                ["ongoing_race"] = 0,
                ["race_ends"] = "06-01-2025 14:00:00",
            },
        };
    }

    private JsonNode Check(LegacyRequest request)
    {
        var needle = request.Text("needle").Trim();
        var kind = request.Int("haystack_type", 1);

        if (kind == 3)
        {
            return Reply.Ok();
        }

        if (needle.Length == 0)
        {
            return Reply.Fail("failed");
        }

        var field = kind == 2 ? "name" : "tag";

        var taken = _clans.All()
            .Select(p => p.Value)
            .OfType<JsonObject>()
            .Any(c => Json.ToText(c[field]).Trim().Equals(needle, StringComparison.OrdinalIgnoreCase));

        return taken ? Reply.Fail("failed") : Reply.Ok();
    }

    private JsonNode ClanInfo(string clanArg, int? me)
    {
        var clanId = clanArg;
        var clan = clanArg.Length > 0 ? _clans.ById(clanArg) : null;

        if (clan is null && me is not null)
        {
            (clanId, clan) = _clans.ClanOf(me.Value);
            clanId ??= string.Empty;
        }

        if (clan is null)
        {
            return Reply.Fail("failed");
        }

        var leader = Json.ToInt(clan["leader"]);
        var leaderData = leader != 0 ? _profiles.Get(leader).Obj("data") : new JsonObject();
        var clanType = Json.ToInt(clan["type"]);

        var info = new JsonObject
        {
            ["order"] = 1,
            ["clan_id"] = Json.ParseInt(clanId),
            ["tag"] = Json.ToText(clan["tag"]),
            ["tag_color"] = Json.ToText(clan["tag_color"], "FFFFFF"),
            ["name"] = Json.ToText(clan["name"]),
            ["clan_exp"] = Json.ToInt(clan["clan_exp"]),
            ["race_exp"] = Json.ToInt(clan["race_exp"]),
            ["leader"] = leader,
            ["leader_nick"] = Json.ToText(leaderData["username"]),
            ["perk_states"] = Json.ToText(leaderData["nickname_color"], "#FFFFFF"),
            ["leader_fname"] = string.Empty,
            ["leader_lname"] = string.Empty,
            ["leader_exp"] = Json.ToInt(leaderData["currentXP"]),
            ["leader_reputation"] = Json.ToInt(leaderData["repa"]),
            ["leader_class"] = Json.ToInt(leaderData["clan_class"]),
            ["leader_kills"] = Json.ToInt(leaderData["killCount"]),
            ["leader_death"] = Json.ToInt(leaderData["deathCount"]),
            ["members_count"] = clan.Arr("members").Count,
            ["max_users"] = _catalog.ClanMembers.GetValueOrDefault(clanType, 10),
            ["race_wins"] = Json.ToInt(clan["race_wins"]),
            ["place"] = 1,
            ["did_request"] = false,
            ["clan_url"] = Json.ToText(clan["link"]),
            ["clan_cr"] = Json.ToInt(clan["clan_cr"]),
            ["clan_gp"] = Json.ToInt(clan["clan_gp"]),
            ["clan_bg"] = Json.ToInt(clan["clan_bg"]),
        };

        var members = new JsonArray();
        var order = 1;

        foreach (var member in clan.Arr("members"))
        {
            members.Add(ClanPerson(Json.ToInt(member), clan, order, true));
            order++;
        }

        var requests = new JsonArray();
        order = 1;

        foreach (var pending in clan.Arr("requests"))
        {
            requests.Add(ClanPerson(Json.ToInt(pending), clan, order, false));
            order++;
        }

        return new JsonObject
        {
            ["result"] = 0,
            ["claninfo"] = info,
            ["clan_userlist"] = members,
            ["clan_requests"] = requests,
        };
    }

    private JsonNode CreateClan(LegacyRequest request, int? me)
    {
        if (me is null)
        {
            return Reply.Fail("failed");
        }

        var tag = request.Text("clan_tag").Trim();
        var name = request.Contains("clan_name")
            ? request.Text("clan_name").Trim()
            : request.Contains("name") ? request.Text("name").Trim() : tag;

        var color = request.Text("clan_color", "FFFFFF").Trim().TrimStart('#');
        var url = request.Text("clan_url").Trim();

        var clanType = request.Contains("clan_type")
            ? request.Int("clan_type", 1)
            : request.Int("type", 1);

        if (clanType == 0)
        {
            clanType = 1;
        }

        var price = _catalog.ClanPrice.GetValueOrDefault(clanType, 500);
        var currency = _catalog.ClanCurrency.GetValueOrDefault(clanType, "gp");

        if (Json.ToInt(_players.Data(me.Value)[currency]) < price)
        {
            return Reply.Fail("not enough " + currency.ToUpperInvariant());
        }

        var newId = _clans.NextClanId();

        _clans.Mutate(data =>
        {
            data[newId.ToString()] = new JsonObject
            {
                ["tag"] = tag,
                ["name"] = name,
                ["tag_color"] = color,
                ["leader"] = me.Value,
                ["members"] = new JsonArray { me.Value },
                ["requests"] = new JsonArray(),
                ["roles"] = new JsonObject { [me.Value.ToString()] = 3 },
                ["ratios"] = new JsonObject(),
                ["type"] = clanType,
                ["clan_exp"] = 0,
                ["race_exp"] = 0,
                ["message"] = string.Empty,
                ["link"] = url,
                ["ratio"] = 0,
                ["skills"] = new JsonObject(),
            };
        });

        _players.MutateData(me.Value, data =>
        {
            data[currency] = Json.ToInt(data[currency]) - price;
            data["clanID"] = newId;
            data["clanTag"] = tag;
            data["clan_name"] = name;
            data["isClanLeader"] = true;
            data["clan_role"] = 3;
        });

        _ledger.Record(me, currency == "gp" ? CurrencyGamePoints : CurrencyCredits, -price, "CLAN CREATE");

        var after = _players.Live(me);

        _logger.LogInformation(
            "clan {ClanId} '{Name}' [{Tag}] created by uid{UserId} for {Price}{Currency}",
            newId,
            name,
            tag,
            me.Value,
            price,
            currency);

        return Reply.Ok(
            ("clan_id", newId.ToString()),
            ("new_gp", after.GamePoints),
            ("new_cr", after.Credits));
    }

    private JsonNode UpgradeClan(string clanArg, int? me)
    {
        var (clanId, clan) = ResolveClan(clanArg, me);

        if (clan is null || me is null)
        {
            return Reply.Fail("failed");
        }

        var newType = Json.ToInt(clan["type"]) + 1;

        if (newType > MaxClanType)
        {
            return Reply.Fail("failed");
        }

        var price = _catalog.ClanPrice.GetValueOrDefault(newType);

        if (Json.ToInt(_players.Data(me.Value)["gp"]) < price)
        {
            return Reply.Fail("failed");
        }

        _clans.Mutate(data =>
        {
            if (data[clanId!] is JsonObject entry)
            {
                entry["type"] = newType;
            }
        });

        _players.MutateData(me.Value, data => data["gp"] = Json.ToInt(data["gp"]) - price);
        _ledger.Record(me, CurrencyGamePoints, -price, "CLAN UPGRADE");

        return Reply.Ok();
    }

    private JsonNode SendRequest(string clanArg, int? me)
    {
        if (me is null || clanArg.Length == 0)
        {
            return Reply.Ok();
        }

        _clans.Mutate(data =>
        {
            if (data[clanArg] is not JsonObject clan)
            {
                return;
            }

            var requests = clan.EnsureArray("requests");

            if (!requests.Any(x => Json.ToInt(x) == me.Value))
            {
                requests.Add(me.Value);
            }
        });

        return Reply.Ok();
    }

    private JsonNode RemoveRequest(string clanArg, int? drop)
    {
        if (drop is null)
        {
            return Reply.Ok();
        }

        _clans.Mutate(data =>
        {
            foreach (var pair in data)
            {
                if (clanArg.Length > 0 && pair.Key != clanArg)
                {
                    continue;
                }

                if (pair.Value is JsonObject clan)
                {
                    clan["requests"] = Without(clan.Arr("requests"), drop.Value);
                }
            }
        });

        return Reply.Ok();
    }

    private JsonNode DeleteAllRequests(string clanArg)
    {
        _clans.Mutate(data =>
        {
            if (data[clanArg] is JsonObject clan)
            {
                clan["requests"] = new JsonArray();
            }
        });

        return Reply.Ok();
    }

    private JsonNode AcceptRequest(string clanArg, int target)
    {
        _clans.Mutate(data =>
        {
            if (data[clanArg] is not JsonObject clan || target == 0)
            {
                return;
            }

            if (!clan.Arr("requests").Any(x => Json.ToInt(x) == target))
            {
                return;
            }

            clan["requests"] = Without(clan.Arr("requests"), target);

            var members = clan.EnsureArray("members");

            if (!members.Any(x => Json.ToInt(x) == target))
            {
                members.Add(target);
                clan.EnsureObject("roles")[target.ToString()] = 0;
            }
        });

        if (target != 0)
        {
            _players.MutateData(target, data =>
            {
                data["clanID"] = Json.ParseInt(clanArg);
                data["clan_role"] = 0;
            });
        }

        return Reply.Ok();
    }

    private JsonNode Kick(string clanArg, int? drop)
    {
        if (drop is null)
        {
            return Reply.Ok();
        }

        var clanId = clanArg.Length > 0 ? clanArg : _clans.ClanOf(drop.Value).ClanId ?? string.Empty;

        _clans.Mutate(data =>
        {
            if (data[clanId] is not JsonObject clan)
            {
                return;
            }

            clan["members"] = Without(clan.Arr("members"), drop.Value);

            if (clan["roles"] is JsonObject roles)
            {
                roles.Remove(drop.Value.ToString());
            }
        });

        _players.MutateData(drop.Value, data =>
        {
            data["clanID"] = 0;
            data["clanTag"] = string.Empty;
            data["clan_name"] = string.Empty;
            data["clan_role"] = 0;
            data["isClanLeader"] = false;
        });

        return Reply.Ok();
    }

    private JsonNode AssignRole(string action, int? me, int target)
    {
        var subject = target != 0 ? target : me ?? 0;
        var (clanId, clan) = _clans.ClanOf(subject);

        if (clan is null || clanId is null)
        {
            return Reply.Fail("failed");
        }

        var role = action switch
        {
            "assign_officer" => 1,
            "assign_alt_leader" => 2,
            "change_leader" => 3,
            _ => 0,
        };

        var cost = action.StartsWith("dismiss", StringComparison.Ordinal) ? 0 : _catalog.ClanAssignGp;

        _clans.Mutate(data =>
        {
            if (data[clanId] is not JsonObject entry)
            {
                return;
            }

            entry.EnsureObject("roles")[target.ToString()] = role;

            if (action == "change_leader")
            {
                entry["leader"] = target;
            }
        });

        if (cost != 0 && me is not null)
        {
            _players.MutateData(me.Value, data => data["gp"] = Json.ToInt(data["gp"]) - cost);
            _ledger.Record(me, CurrencyGamePoints, -cost, "CLAN ASSIGNMENT");
        }

        return Reply.Ok();
    }

    private JsonNode SetField(LegacyRequest request, string action, int? me)
    {
        if (me is null)
        {
            return Reply.Fail("failed");
        }

        var (clanId, clan) = _clans.ClanOf(me.Value);

        if (clan is null || clanId is null)
        {
            return Reply.Fail("failed");
        }

        var field = action switch
        {
            "set_color" => "tag_color",
            "set_link" => "link",
            "post_message" => "message",
            _ => "ratio",
        };

        var value = FirstText(request, "color", "link", "message", "ratio");

        var cost = action switch
        {
            "set_color" => _catalog.ClanColorGp,
            "set_link" => _catalog.ClanLinkGp,
            _ => 0,
        };

        _clans.Mutate(data =>
        {
            if (data[clanId] is JsonObject entry)
            {
                entry[field] = value;
            }
        });

        if (cost != 0)
        {
            _players.MutateData(me.Value, data => data["gp"] = Json.ToInt(data["gp"]) - cost);
            _ledger.Record(me, CurrencyGamePoints, -cost, "CLAN " + action.Replace("_", " ").ToUpperInvariant());
        }

        return Reply.Ok();
    }

    private JsonNode BuySkill(LegacyRequest request, string action, int? me)
    {
        if (me is null)
        {
            return Reply.Fail("not in a clan");
        }

        var (clanId, clan) = _clans.ClanOf(me.Value);

        if (clan is null || clanId is null)
        {
            return Reply.Fail("not in a clan");
        }

        var skillId = request.Int("skill_id", -1);
        var option = request.Int("rent_option", -1);

        if (skillId < 0 || skillId >= _catalog.ClanSkills.Count)
        {
            return Reply.Fail("unknown clan skill");
        }

        var skill = _catalog.ClanSkills[skillId] as JsonObject ?? new JsonObject();
        var premium = Json.ToBool(skill["isPremium"]);
        var field = premium ? "clan_gp" : "clan_cr";

        int price;
        long rentEnd;
        var rentDays = 0;

        if (action == "rentskill")
        {
            var prices = skill.Arr("rentPrice");
            var days = skill.Arr("rentTime");

            if (option < 0 || option >= prices.Count)
            {
                return Reply.Fail("bad rent option");
            }

            price = Json.ToInt(prices[option]);
            rentDays = option < days.Count ? Math.Max(1, Json.ToInt(days[option], 1)) : 1;
            rentEnd = _clock.UnixSeconds + rentDays * 86400L;
        }
        else
        {
            price = Json.ToInt(premium ? skill["GP"] : skill["CR"]);
            rentEnd = 0;
        }

        if (Json.ToInt(clan[field]) < price)
        {
            return Reply.Fail("clan has not enough " + (premium ? "GP" : "CR"));
        }

        _clans.Mutate(data =>
        {
            if (data[clanId] is not JsonObject entry)
            {
                return;
            }

            entry[field] = Json.ToInt(entry[field]) - price;
            entry.EnsureObject("skills")[skillId.ToString()] = rentEnd;
        });

        foreach (var member in clan.Arr("members"))
        {
            _economy.GrantClanSkill(Json.ToInt(member), skillId, rentEnd);
        }

        _logger.LogInformation(
            "clan {ClanId} skill {SkillId} {Term} for {Price}{Currency}",
            clanId,
            skillId,
            rentEnd == 0 ? "permanent" : $"{rentDays}d",
            price,
            premium ? "gp" : "cr");

        return rentEnd != 0 ? Reply.Ok(("rentEnd", rentEnd)) : Reply.Ok();
    }

    private JsonNode Transfer(LegacyRequest request, int? me)
    {
        var credits = Math.Max(0, request.Int("credits"));
        var gamePoints = Math.Max(0, request.Int("gp"));

        if (me is null)
        {
            return Reply.Fail("not in a clan");
        }

        var (clanId, clan) = _clans.ClanOf(me.Value);

        if (clan is null || clanId is null)
        {
            return Reply.Fail("not in a clan");
        }

        if (credits == 0 && gamePoints == 0)
        {
            return Reply.Fail("nothing to transfer");
        }

        var data = _players.Data(me.Value);

        if (Json.ToInt(data["cr"]) < credits)
        {
            return Reply.Fail("Not enough CR");
        }

        if (Json.ToInt(data["gp"]) < gamePoints)
        {
            return Reply.Fail("Not enough GP");
        }

        _clans.Mutate(store =>
        {
            if (store[clanId] is not JsonObject entry)
            {
                return;
            }

            entry["clan_cr"] = Json.ToInt(entry["clan_cr"]) + credits;
            entry["clan_gp"] = Json.ToInt(entry["clan_gp"]) + gamePoints;
        });

        var afterCredits = 0;
        var afterGamePoints = 0;

        _players.MutateData(me.Value, profile =>
        {
            afterCredits = Json.ToInt(profile["cr"]) - credits;
            afterGamePoints = Json.ToInt(profile["gp"]) - gamePoints;
            profile["cr"] = afterCredits;
            profile["gp"] = afterGamePoints;
        });

        if (credits != 0)
        {
            _ledger.Record(me, CurrencyCredits, -credits, "CLAN BALANCE");
        }

        if (gamePoints != 0)
        {
            _ledger.Record(me, CurrencyGamePoints, -gamePoints, "CLAN BALANCE");
        }

        _logger.LogInformation(
            "clan {ClanId} balance +{Credits}cr +{GamePoints}gp from uid{UserId}",
            clanId,
            credits,
            gamePoints,
            me.Value);

        return Reply.Ok(("new_cr", afterCredits), ("new_gp", afterGamePoints));
    }

    private JsonNode ClanTransactions(string clanArg, int? me)
    {
        var clan = clanArg.Length > 0
            ? _clans.ById(clanArg)
            : me is not null ? _clans.ClanOf(me.Value).Clan : null;

        return new JsonObject
        {
            ["data"] = true,
            ["result"] = 0,
            ["transactions"] = Json.CloneArray(clan?["transactions"]),
        };
    }

    private (string? ClanId, JsonObject? Clan) ResolveClan(string clanArg, int? me)
    {
        if (clanArg.Length > 0)
        {
            return (clanArg, _clans.ById(clanArg));
        }

        return me is not null ? _clans.ClanOf(me.Value) : (null, null);
    }

    private JsonObject ClanRow(string clanId, JsonObject clan, int order, bool top)
    {
        var leader = Json.ToInt(clan["leader"]);
        var leaderData = leader != 0 ? _profiles.Get(leader).Obj("data") : new JsonObject();

        var row = new JsonObject
        {
            ["clan_id"] = clanId,
            ["tag"] = Json.ToText(clan["tag"]),
            ["tag_color"] = Json.ToText(clan["tag_color"], "FFFFFF"),
            ["name"] = Json.ToText(clan["name"]),
            ["platform"] = "1",
            ["clan_exp"] = Json.ToInt(clan["clan_exp"]).ToString(),
            ["race_exp"] = Json.ToInt(clan["race_exp"]).ToString(),
            ["leader_nick"] = Json.ToText(leaderData["username"]),
            ["leader_exp"] = ((double)Json.ToInt(leaderData["currentXP"])).ToString("F2", CultureInfo.InvariantCulture),
            ["leader_fname"] = null,
            ["leader_lname"] = null,
            ["leader_class"] = Json.ToInt(leaderData["clan_class"]).ToString(),
            ["members_count"] = clan.Arr("members").Count.ToString(),
            ["did_request"] = false,
            ["order"] = order,
            ["banned"] = false,
        };

        if (top)
        {
            row["awarded"] = "0";
        }

        return row;
    }

    private JsonObject ClanPerson(int userId, JsonObject clan, int order, bool member)
    {
        var data = _profiles.Get(userId).Obj("data");

        var row = new JsonObject
        {
            ["order"] = order,
            ["user_id"] = userId,
            ["social_id"] = Json.ToText(data["social_id"], userId.ToString()),
            ["curr_xp"] = Json.ToInt(data["currentXP"]),
            ["class"] = Json.ToInt(data["clan_class"]),
            ["user_name"] = Json.ToText(data["username"]),
            ["perk_states"] = Json.ToText(data["nickname_color"], "#FFFFFF"),
            ["first_name"] = string.Empty,
            ["last_name"] = string.Empty,
            ["kill_count"] = Json.ToInt(data["killCount"]),
            ["death_count"] = Json.ToInt(data["deathCount"]),
            ["repa"] = Json.ToInt(data["repa"]),
            ["prokills"] = 0,
            ["achievements"] = data.Arr("achievements").Count(a => Json.ToInt(a) > 0),
            ["contracts"] = 0,
        };

        if (member)
        {
            row["earn"] = Json.ToInt(data["clan_earn"]);
            row["earn_proc"] = Json.ToInt(data["clan_earn_proc"]);
            row["clan_role"] = Json.ToInt(clan.Obj("roles")[userId.ToString()]);
        }
        else
        {
            row["place"] = order;
        }

        return row;
    }

    private static JsonArray Without(JsonArray source, int value)
    {
        var result = new JsonArray();

        foreach (var item in source)
        {
            if (Json.ToInt(item) != value)
            {
                result.Add(Json.Clone(item));
            }
        }

        return result;
    }

    private static string FirstText(LegacyRequest request, params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = request.Value(key);

            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }
        }

        return string.Empty;
    }
}
