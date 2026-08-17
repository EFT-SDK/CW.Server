using CW.Server.Configuration;
using CW.Server.Infrastructure;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Text.Json.Nodes;

namespace CW.Server.Storage;

public interface ITransactionLedger
{
    JsonArray Read(int userId);

    void Record(int? userId, int currency, int amount, string comment);
}

public sealed class TransactionLedger : ITransactionLedger
{
    private readonly ServerPaths _paths;
    private readonly IJsonFileStore _files;
    private readonly StateLock _lock;
    private readonly IClock _clock;
    private readonly ServerOptions _options;

    public TransactionLedger(
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

    public JsonArray Read(int userId)
    {
        using (_lock.Enter())
        {
            return _files.LoadArray(PathFor(userId));
        }
    }

    public void Record(int? userId, int currency, int amount, string comment)
    {
        if (userId is null)
        {
            return;
        }

        using (_lock.Enter())
        {
            var rows = _files.LoadArray(PathFor(userId.Value));

            var entry = new JsonObject
            {
                ["currency"] = currency,
                ["amount"] = amount,
                ["comment"] = comment,
                ["date"] = _clock.Now.ToString("HH:mm dd-MM-yyyy", CultureInfo.InvariantCulture),
            };

            rows.Insert(0, entry);

            while (rows.Count > _options.TransactionLimit)
            {
                rows.RemoveAt(rows.Count - 1);
            }

            _files.Save(PathFor(userId.Value), rows);
        }
    }

    private string PathFor(int userId) => Path.Combine(_paths.State, "transactions", $"{userId}.json");
}
