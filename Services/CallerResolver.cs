using CW.Server.Storage;
using CW.Server.Transport;

namespace CW.Server.Services;

public interface ICallerResolver
{
    int? Resolve(LegacyRequest request);

    int ResolveOrZero(LegacyRequest request);
}

public sealed class CallerResolver : ICallerResolver
{
    private readonly ISessionRegistry _sessions;

    public CallerResolver(ISessionRegistry sessions)
    {
        _sessions = sessions;
    }

    public int ResolveOrZero(LegacyRequest request) => Resolve(request) ?? 0;

    public int? Resolve(LegacyRequest request)
    {
        foreach (var key in new[] { "user_id", "uid" })
        {
            var raw = request.Value(key);
            if (!string.IsNullOrEmpty(raw) && int.TryParse(raw, out var explicitId))
            {
                return explicitId;
            }
        }

        var identified = ResolveBySignature(request);
        if (identified.HasValue)
        {
            return identified;
        }

        return _sessions.LastUserId ?? _sessions.SingleActiveUser;
    }

    private int? ResolveBySignature(LegacyRequest request)
    {
        var signature = (request.Value("sig") ?? string.Empty).ToLowerInvariant();

        if (signature.Length == 0 || request.RawQuery.Length == 0)
        {
            return null;
        }

        var candidates = BuildCandidates(request);
        var posts = new[] { "json=" + request.RawBody, string.Empty, request.RawBody };

        foreach (var (token, userId) in _sessions.Snapshot())
        {
            foreach (var candidate in candidates)
            {
                foreach (var post in posts)
                {
                    if (RequestSignature.Compute(token, candidate, post) == signature)
                    {
                        return userId;
                    }
                }
            }
        }

        return null;
    }

    private static List<string> BuildCandidates(LegacyRequest request)
    {
        var separatorIndex = request.RawQuery.IndexOf("&sig=", StringComparison.Ordinal);
        var baseQuery = separatorIndex >= 0 ? request.RawQuery[..separatorIndex] : request.RawQuery;
        var uri = baseQuery.StartsWith('?') ? baseQuery : "?" + baseQuery;
        var path = request.Path.TrimStart('/');

        var candidates = new List<string> { uri, uri.TrimStart('?') };

        if (path.Length > 0)
        {
            candidates.Add(path + uri);
            candidates.Add(path + uri.TrimStart('?'));
            candidates.Add("/" + path + uri);
            candidates.Add(path + "?" + baseQuery.TrimStart('?'));
        }

        return candidates;
    }
}
