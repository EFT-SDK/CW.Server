using CW.Server.Configuration;
using CW.Server.Data;
using CW.Server.Endpoints;
using CW.Server.Infrastructure;
using CW.Server.Transport;
using Microsoft.Extensions.Options;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CW.Server.Routing;

public readonly record struct RouteResult(JsonNode? Payload, byte[]? Raw)
{
    public static RouteResult Json(JsonNode? payload) => new(payload, null);

    public static RouteResult Bytes(byte[] raw) => new(null, raw);
}

public interface ILegacyRouter
{
    RouteResult Dispatch(LegacyRequest request);

    string Describe(LegacyRequest request);
}

public sealed partial class LegacyRouter : ILegacyRouter
{
    private readonly AccountEndpoints _accounts;
    private readonly EconomyEndpoints _economy;
    private readonly RouletteEndpoints _roulette;
    private readonly SocialEndpoints _social;
    private readonly ClanEndpoints _clans;
    private readonly PlayerServiceEndpoints _services;
    private readonly MasteringEndpoints _mastering;
    private readonly IGameDataProvider _data;
    private readonly ServerOptions _options;

    private readonly Dictionary<string, Func<LegacyRequest, JsonNode>> _actions;

    public LegacyRouter(
        AccountEndpoints accounts,
        EconomyEndpoints economy,
        RouletteEndpoints roulette,
        SocialEndpoints social,
        ClanEndpoints clans,
        PlayerServiceEndpoints services,
        MasteringEndpoints mastering,
        IGameDataProvider data,
        IOptions<ServerOptions> options)
    {
        _accounts = accounts;
        _economy = economy;
        _roulette = roulette;
        _social = social;
        _clans = clans;
        _services = services;
        _mastering = mastering;
        _data = data;
        _options = options.Value;

        _actions = new Dictionary<string, Func<LegacyRequest, JsonNode>>(StringComparer.Ordinal)
        {
            ["init"] = _accounts.Init,
            ["load"] = _accounts.Load,
            ["save"] = _accounts.Save,
            ["idload"] = _accounts.IdLoad,
            ["idsave"] = _accounts.IdSave,
            ["keepalive"] = _accounts.KeepAlive,
            ["getattempts"] = _accounts.GetAttempts,
            ["recordfriends"] = _accounts.RecordFriends,
            ["gethosts"] = _accounts.GetHosts,

            ["weaponunlock"] = _economy.WeaponUnlock,
            ["premiumweaponunlock"] = _economy.PremiumWeaponUnlock,
            ["buykit"] = _economy.BuyKit,
            ["buyset"] = _economy.BuySet,
            ["buybox"] = _economy.BuyBox,
            ["buysp"] = _economy.BuySkillPoint,
            ["buynickchanges"] = _economy.BuyNickChanges,
            ["buy_wtask"] = _economy.BuyWeaponTask,
            ["buywtask"] = _economy.BuyWeaponTask,
            ["buy"] = _economy.BankBuy,
            ["skillunlock"] = _economy.SkillUnlock,
            ["buyskill"] = _economy.SkillUnlock,
            ["repair"] = _economy.Repair,
            ["repair_all"] = _economy.Repair,
            ["skipcontract"] = _economy.SkipContract,

            ["spinroulette"] = _roulette.Spin,
            ["buyroulette"] = _roulette.Buy,

            ["getrating"] = _social.GetRating,
            ["getseasonrating"] = _social.GetRating,
            ["overview"] = _social.Overview,
            ["wl_list"] = _social.WatchlistList,
            ["wl_add"] = _social.WatchlistAdd,
            ["wl_remove"] = _social.WatchlistRemove,
            ["profilelink"] = _social.ProfileLink,
            ["vote"] = _social.Vote,
            ["hall_of_fame_unit"] = _social.HallOfFame,
            ["own"] = _social.TakeOwnership,

            ["getclanrating"] = _clans.GetClanRating,
            ["resetclanrequests"] = _clans.ResetClanRequests,

            ["get_transactions"] = _services.GetTransactions,
            ["getbalance"] = _services.GetBalance,
            ["buygp"] = _services.BuyGamePoints,
            ["payments_pending"] = _services.PaymentsPending,
            ["clearnewlevel"] = _services.ClearNewLevel,
            ["promo"] = _services.Promo,
            ["reset_skills"] = _services.ResetSkills,
            ["reset_skillscr"] = _services.ResetSkills,
            ["skillrent"] = _services.SkillRent,
            ["get_contracts"] = _services.GetContracts,
            ["initcontracts"] = _services.InitContracts,
            ["performcontracts"] = _services.PerformContracts,
            ["getdailybonus"] = _services.DailyBonus,
        };
    }

    public string Describe(LegacyRequest request)
    {
        var action = request.Action;

        if (action.Length > 0)
        {
            return action;
        }

        var admQuery = request.AdmQuery;
        return admQuery.Length > 0 ? admQuery : request.Path;
    }

    public RouteResult Dispatch(LegacyRequest request)
    {
        if (request.Path.EndsWith("adm.php", StringComparison.OrdinalIgnoreCase))
        {
            return DispatchAdm(request);
        }

        if (request.Path.Contains("/ms/", StringComparison.OrdinalIgnoreCase)
            || request.Path.EndsWith("/ms", StringComparison.OrdinalIgnoreCase))
        {
            return RouteResult.Json(request.Action == "register"
                ? _accounts.MasterServerRegister(request)
                : _accounts.MasterServerList(request));
        }

        if (request.Path.EndsWith("getcontentinfo.php", StringComparison.OrdinalIgnoreCase))
        {
            return RouteResult.Json(_accounts.ContentInfo(request));
        }

        if (request.Path.EndsWith("checknick.php", StringComparison.OrdinalIgnoreCase))
        {
            return RouteResult.Json(_accounts.CheckNick(request));
        }

        if (request.Path.EndsWith("clans.php", StringComparison.OrdinalIgnoreCase))
        {
            return RouteResult.Json(_clans.Route(request));
        }

        if (_actions.TryGetValue(request.Action, out var handler))
        {
            return RouteResult.Json(handler(request));
        }

        return RouteResult.Json(Reply.Ok());
    }

    private RouteResult DispatchAdm(LegacyRequest request)
    {
        var query = request.AdmQuery;

        if (query.StartsWith("setting/", StringComparison.Ordinal))
        {
            return ServeSetting(query["setting/".Length..]);
        }

        const string customizationPrefix = "customization/";
        var prefixIndex = query.IndexOf(customizationPrefix, StringComparison.Ordinal);

        if (prefixIndex < 0)
        {
            return RouteResult.Json(Reply.Ok());
        }

        var sub = query[(prefixIndex + customizationPrefix.Length)..];

        if (sub.StartsWith("player/main_load", StringComparison.Ordinal))
        {
            return RouteResult.Json(_mastering.MainLoad(request));
        }

        if (sub.StartsWith("player/load_suits", StringComparison.Ordinal))
        {
            return RouteResult.Json(_mastering.LoadSuits(request));
        }

        if (sub.StartsWith("player/load", StringComparison.Ordinal))
        {
            return RouteResult.Json(_mastering.PlayerLoad(request));
        }

        var saveSuits = SaveSuitsPattern().Match(sub);
        if (saveSuits.Success)
        {
            return RouteResult.Json(_mastering.SaveSuits(
                request,
                int.Parse(saveSuits.Groups[1].Value),
                int.Parse(saveSuits.Groups[2].Value)));
        }

        var setCamo = SetCamoPattern().Match(sub);
        if (setCamo.Success)
        {
            return RouteResult.Json(_mastering.SetCamoInfo(
                request,
                int.Parse(setCamo.Groups[1].Value),
                int.Parse(setCamo.Groups[2].Value),
                int.Parse(setCamo.Groups[3].Value)));
        }

        var metaBuy = MetaBuyPattern().Match(sub);
        if (metaBuy.Success)
        {
            return RouteResult.Json(_mastering.BuyMeta(
                request,
                int.Parse(metaBuy.Groups[1].Value),
                int.Parse(metaBuy.Groups[2].Value)));
        }

        var modBuy = ModBuyPattern().Match(sub);
        if (modBuy.Success)
        {
            return RouteResult.Json(_mastering.BuyMod(
                request,
                int.Parse(modBuy.Groups[1].Value),
                int.Parse(modBuy.Groups[2].Value)));
        }

        var camoBuy = CamoBuyPattern().Match(sub);
        if (camoBuy.Success)
        {
            return RouteResult.Json(_economy.BuyCamouflage(request, int.Parse(camoBuy.Groups[1].Value)));
        }

        var mpBuy = MasteringPointBuyPattern().Match(sub);
        if (mpBuy.Success)
        {
            return RouteResult.Json(_mastering.BuyMasteringPoints(request, int.Parse(mpBuy.Groups[1].Value)));
        }

        var wtaskUnlock = WeaponTaskUnlockPattern().Match(sub);
        if (wtaskUnlock.Success)
        {
            return RouteResult.Json(_mastering.UnlockWeaponTaskMeta(
                request,
                int.Parse(wtaskUnlock.Groups[1].Value)));
        }

        var setWtask = SetWeaponTaskPattern().Match(sub);
        if (setWtask.Success)
        {
            return RouteResult.Json(_mastering.SetWeaponTaskInfo(request, int.Parse(setWtask.Groups[1].Value)));
        }

        if (sub.StartsWith("player/save", StringComparison.Ordinal))
        {
            return RouteResult.Json(_mastering.Save(request));
        }

        if (sub.StartsWith("server/load", StringComparison.Ordinal))
        {
            var explicitUser = ServerLoadPattern().Match(sub);

            int? userId = explicitUser.Success
                ? int.Parse(explicitUser.Groups[1].Value)
                : null;

            return RouteResult.Json(_mastering.ServerLoad(request, userId));
        }

        if (sub.StartsWith("server/save", StringComparison.Ordinal))
        {
            return RouteResult.Json(_mastering.Save(request));
        }

        return RouteResult.Json(Reply.Ok());
    }

    private RouteResult ServeSetting(string name)
    {
        if (!_data.BackendExists(name))
        {
            return RouteResult.Json(new JsonObject());
        }

        if (name == "getGlobals" && _options.HttpDebug)
        {
            var globals = Json.CloneObject(_data.Backend(name));
            globals["n_httpDebug"] = true;
            return RouteResult.Json(globals);
        }

        var raw = _data.BackendRaw(name);
        return raw is not null ? RouteResult.Bytes(raw) : RouteResult.Json(new JsonObject());
    }

    [GeneratedRegex(@"^player/save_suits/(-?\d+)/(-?\d+)")]
    private static partial Regex SaveSuitsPattern();

    [GeneratedRegex(@"^player/set_camo_info/(-?\d+)/(-?\d+)/(-?\d+)")]
    private static partial Regex SetCamoPattern();

    [GeneratedRegex(@"^player/meta/buy/(-?\d+)/(-?\d+)")]
    private static partial Regex MetaBuyPattern();

    [GeneratedRegex(@"^player/mod/buy/(-?\d+)/(-?\d+)")]
    private static partial Regex ModBuyPattern();

    [GeneratedRegex(@"^player/camo/buy/(-?\d+)")]
    private static partial Regex CamoBuyPattern();

    [GeneratedRegex(@"^player/mp/buy/(-?\d+)")]
    private static partial Regex MasteringPointBuyPattern();

    [GeneratedRegex(@"^player/wtask/(?:server_)?unlock/(-?\d+)")]
    private static partial Regex WeaponTaskUnlockPattern();

    [GeneratedRegex(@"^player/set_wtask_info/(-?\d+)")]
    private static partial Regex SetWeaponTaskPattern();

    [GeneratedRegex(@"server/load/(-?\d+)")]
    private static partial Regex ServerLoadPattern();
}
