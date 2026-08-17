using CW.Server.Configuration;
using CW.Server.Infrastructure;
using Microsoft.Extensions.Options;
using System.Text.Json.Nodes;

namespace CW.Server.Storage;

public interface IHostRegistry
{
    JsonObject Register(JsonNode? info, string clientIp);

    IReadOnlyList<JsonObject> Live(int ttlSeconds = 90);
}

public sealed class HostRegistry : IHostRegistry
{
    private readonly IClock _clock;
    private readonly ServerOptions _options;
    private readonly StateLock _lock;
    private readonly Dictionary<string, (JsonObject Host, long Seen)> _hosts = new(StringComparer.Ordinal);

    public HostRegistry(IClock clock, IOptions<ServerOptions> options, StateLock stateLock)
    {
        _clock = clock;
        _options = options.Value;
        _lock = stateLock;
    }

    public static JsonObject DefaultHost() => new()
    {
        ["name"] = "Local Server",
        ["playerCount"] = 0,
        ["spectatorCount"] = 0,
        ["loadPlayerCount"] = 0,
        ["maxPlayers"] = 16,
        ["minLevel"] = 0,
        ["maxLevel"] = 70,
        ["mapIndex"] = 8,
        ["gameMode"] = 1,
        ["ranked"] = false,
        ["hardcore"] = false,
        ["skip"] = false,
        ["testVip"] = false,
        ["hidden"] = false,
        ["forceNAT"] = false,
        ["password"] = false,
        ["debug"] = string.Empty,
        ["ip"] = "127.0.0.1",
        ["port"] = 27015,
        ["platform"] = 0,
    };

    public JsonObject Register(JsonNode? info, string clientIp)
    {
        var host = DefaultHost();

        if (info is JsonObject supplied)
        {
            foreach (var pair in supplied)
            {
                host[pair.Key] = Json.Clone(pair.Value);
            }
        }

        var advertised = Json.ToText(host["ip"]);
        if (string.IsNullOrEmpty(advertised))
        {
            advertised = clientIp;
        }

        if (IsPrivate(advertised))
        {
            if (!IsPrivate(clientIp))
            {
                host["ip"] = clientIp;
            }
            else if (!string.IsNullOrWhiteSpace(_options.PublicIp))
            {
                host["ip"] = _options.PublicIp;
            }
            else
            {
                host["ip"] = advertised;
            }
        }

        var key = $"{Json.ToText(host["ip"])}:{Json.ToText(host["port"])}";

        using (_lock.Enter())
        {
            _hosts[key] = (host, _clock.UnixSeconds);
        }

        return host;
    }

    public IReadOnlyList<JsonObject> Live(int ttlSeconds = 90)
    {
        var now = _clock.UnixSeconds;

        using (_lock.Enter())
        {
            var dead = _hosts.Where(p => now - p.Value.Seen > ttlSeconds).Select(p => p.Key).ToList();
            foreach (var key in dead)
            {
                _hosts.Remove(key);
            }

            return _hosts.Values.Select(v => v.Host).ToList();
        }
    }

    public static bool IsPrivate(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip))
        {
            return true;
        }

        var parts = ip.Split('.');
        if (parts.Length < 2 || !int.TryParse(parts[0], out var a) || !int.TryParse(parts[1], out var b))
        {
            return true;
        }

        return a is 10 or 127
               || (a == 172 && b is >= 16 and <= 31)
               || (a == 192 && b == 168)
               || (a == 169 && b == 254);
    }
}
