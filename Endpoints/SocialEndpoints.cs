using CW.Server.Configuration;
using CW.Server.Data;
using CW.Server.Infrastructure;
using CW.Server.Services;
using CW.Server.Storage;
using CW.Server.Transport;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace CW.Server.Endpoints;

public sealed class SocialEndpoints
{
    private readonly IAccountRepository _accounts;
    private readonly IProfileRepository _profiles;
    private readonly IWatchlistRepository _watchlist;
    private readonly IClanRepository _clans;
    private readonly PlayerService _players;
    private readonly GameCatalog _catalog;
    private readonly ServerOptions _options;
    private readonly IClock _clock;

    public SocialEndpoints(
        IAccountRepository accounts,
        IProfileRepository profiles,
        IWatchlistRepository watchlist,
        IClanRepository clans,
        PlayerService players,
        GameCatalog catalog,
        IOptions<ServerOptions> options,
        IClock clock)
    {
        _accounts = accounts;
        _profiles = profiles;
        _watchlist = watchlist;
        _clans = clans;
        _players = players;
        _catalog = catalog;
        _options = options.Value;
        _clock = clock;
    }

    public JsonNode GetRating(LegacyRequest request)
    {
        var search = request.Text("search").Trim().ToLowerInvariant();
        var hardcore = request.Text("hc") == "1";
        var friendsOnly = request.Text("friends") == "1";
        var me = _players.Caller(request);

        var rows = AllAccounts();

        if (friendsOnly)
        {
            var allowed = _watchlist.Read(me ?? 0).Select(x => Json.ToInt(x)).ToHashSet();
            rows = rows.Where(r => allowed.Contains(r.UserId)).ToList();
        }

        var sortKey = hardcore ? "hcCurrentXP" : "currentXP";
        rows = rows
            .OrderByDescending(r => Json.ToInt(r.Profile.Obj("data")[sortKey]))
            .ToList();

        if (search.Length > 0)
        {
            rows = rows
                .Where(r => Json.ToText(r.Profile.Obj("data")["username"]).ToLowerInvariant().Contains(search))
                .ToList();
        }

        var output = new JsonArray();
        var place = 1;

        foreach (var row in rows.Take(_options.RatingPageSize))
        {
            output.Add(RatingRow(row.UserId, row.Profile, place, search.Length > 0));
            place++;
        }

        return new JsonObject { ["data"] = output, ["result"] = 0 };
    }

    public JsonNode Overview(LegacyRequest request)
    {
        var userId = request.Int("user_id");
        var known = _accounts.All().Any(a => a.UserId == userId);

        if (userId == 0 || !known)
        {
            return Reply.Fail("failed");
        }

        var profile = _profiles.Get(userId);
        var data = Json.CloneObject(profile["data"]);

        if (data["awards"] is null)
        {
            data["awards"] = new JsonArray();
        }

        if (data["banned"] is null)
        {
            data["banned"] = 0;
        }

        data["currentXP"] = (double)Json.ToInt(data["currentXP"]);

        return new JsonObject
        {
            ["data"] = data,
            ["stats"] = Json.Clone(profile["stats"]) ?? new JsonObject(),
            ["social"] = Json.Clone(profile["social"]) ?? new JsonObject(),
            ["result"] = 0,
            ["hops_secret_key"] = Md5(userId.ToString()),
        };
    }

    public JsonNode WatchlistList(LegacyRequest request)
    {
        var me = _players.CallerIgnoringQuery(request) ?? 0;
        var rows = new JsonArray();

        foreach (var entry in _watchlist.Read(me))
        {
            var targetId = Json.ToInt(entry);
            if (targetId == 0)
            {
                continue;
            }

            var data = _profiles.Get(targetId).Obj("data");
            var kills = Json.ToInt(data["killCount"]);
            var deaths = Json.ToInt(data["deathCount"]);
            var (_, clan) = _clans.ClanOf(targetId);

            rows.Add(new JsonObject
            {
                ["user_id"] = targetId,
                ["level"] = _catalog.LevelForXp(Json.ToInt(data["currentXP"])),
                ["class"] = Json.ToInt(data["clan_class"]),
                ["exp"] = Json.ToInt(data["currentXP"]),
                ["kills"] = kills,
                ["deaths"] = deaths,
                ["kd"] = deaths != 0 ? kills / (double)deaths : kills,
                ["repa"] = Json.ToInt(data["repa"]),
                ["canvote"] = true,
                ["online"] = false,
                ["onlineType"] = 0,
                ["ip"] = string.Empty,
                ["port"] = 0,
                ["clanTag"] = ClanTag(clan),
            });
        }

        return Reply.Ok(("list", rows));
    }

    public JsonNode WatchlistAdd(LegacyRequest request)
    {
        var me = _players.CallerIgnoringQuery(request) ?? 0;
        var target = request.Int("user_id");

        if (target == 0)
        {
            return Reply.Fail("failed");
        }

        if (_accounts.All().Any(a => a.UserId == target))
        {
            var data = _profiles.Get(target).Obj("data");

            if (Json.ToInt(data["wl_perm"], 1) == 0)
            {
                return new JsonObject { ["result"] = 1002, ["msg"] = "Adding denied by user." };
            }
        }

        var current = _watchlist.Read(me).Select(x => Json.ToInt(x)).ToList();

        if (!current.Contains(target))
        {
            current.Add(target);
        }

        _watchlist.Write(me, ToArray(current));
        return Reply.Ok();
    }

    public JsonNode WatchlistRemove(LegacyRequest request)
    {
        var me = _players.CallerIgnoringQuery(request) ?? 0;
        var target = request.Int("user_id");

        var remaining = _watchlist.Read(me)
            .Select(x => Json.ToInt(x))
            .Where(id => id != target)
            .ToList();

        _watchlist.Write(me, ToArray(remaining));
        return Reply.Ok();
    }

    public JsonNode ProfileLink(LegacyRequest request)
    {
        return Reply.Ok(("msg", "https://www.contractwarsgame.com/"));
    }

    public JsonNode Vote(LegacyRequest request)
    {
        var me = _players.CallerIgnoringQuery(request);
        var target = request.Int("user_id");

        var votes = 0;
        var reputation = 0;

        if (me is not null)
        {
            _players.MutateData(me.Value, data =>
            {
                votes = Math.Max(0, Json.ToInt(data["votes"], 999) - 1);
                data["votes"] = votes;
            });
        }

        if (target != 0)
        {
            _players.MutateData(target, data =>
            {
                reputation = Json.ToInt(data["repa"]) + 1;
                data["repa"] = reputation;
            });
        }

        return Reply.Ok(
            ("message", "Vote accepted"),
            ("new_votes", votes),
            ("new_repa", reputation));
    }

    public JsonNode HallOfFame(LegacyRequest request)
    {
        var now = _clock.Now.UtcDateTime;

        return Reply.Ok(
            ("year", request.Int("year", now.Year)),
            ("month", request.Int("month", now.Month)),
            ("unit", new JsonArray()),
            ("leftArrow", false),
            ("rightArrow", false));
    }

    public JsonNode TakeOwnership(LegacyRequest request)
    {
        return Reply.Ok();
    }

    public JsonObject RatingRow(int userId, JsonObject profile, int place, bool stringly)
    {
        var data = profile.Obj("data");
        var kills = Json.ToInt(data["killCount"]);
        var deaths = Json.ToInt(data["deathCount"]);
        var (_, clan) = _clans.ClanOf(userId);
        var kd = deaths != 0 ? kills / (double)deaths : kills;

        var row = new JsonObject
        {
            ["user_id"] = stringly ? userId.ToString() : userId,
            ["kd"] = stringly ? kd.ToString(CultureInfo.InvariantCulture) : kd,
            ["repa"] = stringly ? Json.ToInt(data["repa"]).ToString() : Json.ToInt(data["repa"]),
            ["platform"] = "1",
            ["place"] = place,
            ["clanTag"] = ClanTag(clan),
            ["first_name"] = string.Empty,
            ["last_name"] = string.Empty,
            ["exp"] = Json.ToInt(data["currentXP"]),
            ["kills"] = stringly ? kills.ToString() : kills,
            ["deaths"] = stringly ? deaths.ToString() : deaths,
            ["class"] = Json.ToInt(data["clan_class"], 1),
            ["name"] = Json.ToText(data["username"], $"Player{userId}"),
            ["level"] = _catalog.LevelForXp(Json.ToInt(data["currentXP"])),
            ["canvote"] = true,
            ["online"] = false,
            ["lastOnline"] = "MC-",
            ["onlineData"] = string.Empty,
            ["onlineType"] = 0,
        };

        return row;
    }

    public static string ClanTag(JsonObject? clan)
    {
        if (clan is null)
        {
            return string.Empty;
        }

        return $"[#{Json.ToText(clan["tag_color"], "FFFFFF")}]{Json.ToText(clan["tag"])}";
    }

    private List<(int UserId, JsonObject Profile)> AllAccounts()
    {
        var result = new List<(int, JsonObject)>();

        foreach (var account in _accounts.All())
        {
            result.Add((account.UserId, _profiles.Get(account.UserId)));
        }

        return result;
    }

    private static JsonArray ToArray(IEnumerable<int> values)
    {
        var array = new JsonArray();

        foreach (var value in values)
        {
            array.Add(value);
        }

        return array;
    }

    private static string Md5(string input)
    {
        return Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
    }
}
