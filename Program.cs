using CW.Server.Configuration;
using CW.Server.Data;
using CW.Server.Endpoints;
using CW.Server.Infrastructure;
using CW.Server.Routing;
using CW.Server.Services;
using CW.Server.Storage;
using CW.Server.Transport;

var builder = WebApplication.CreateBuilder(args);

var dataRoot = ServerDataLocator.Resolve(
    builder.Configuration[$"{ServerOptions.SectionName}:DataRoot"],
    AppContext.BaseDirectory);

var paths = new ServerPaths(dataRoot);
paths.EnsureCreated();

builder.Configuration.AddJsonFile(Path.Combine(dataRoot, "server.json"), optional: true, reloadOnChange: true);
builder.Configuration.AddEnvironmentVariables("CW_");

builder.Services.Configure<ServerOptions>(builder.Configuration.GetSection(ServerOptions.SectionName));
builder.Services.PostConfigure<ServerOptions>(options => options.DataRoot = dataRoot);

var options = new ServerOptions();
builder.Configuration.GetSection(ServerOptions.SectionName).Bind(options);

builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.AddServerHeader = false;
    kestrel.AllowSynchronousIO = false;
});

builder.WebHost.UseUrls($"http://{options.Host}:{options.Port}");

builder.Services.AddSingleton(paths);
builder.Services.AddSingleton<StateLock>();
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<IJsonFileStore, JsonFileStore>();

builder.Services.AddSingleton<IGameDataProvider, GameDataProvider>();
builder.Services.AddSingleton<GameCatalog>();

builder.Services.AddSingleton<IAccountRepository, AccountRepository>();
builder.Services.AddSingleton<ISessionRegistry, SessionRegistry>();
builder.Services.AddSingleton<IProfileRepository, ProfileRepository>();
builder.Services.AddSingleton<ICustomizationRepository, CustomizationRepository>();
builder.Services.AddSingleton<IAttemptsRepository, AttemptsRepository>();
builder.Services.AddSingleton<ITransactionLedger, TransactionLedger>();
builder.Services.AddSingleton<IWatchlistRepository, WatchlistRepository>();
builder.Services.AddSingleton<IClanRepository, ClanRepository>();
builder.Services.AddSingleton<IHostRegistry, HostRegistry>();

builder.Services.AddSingleton<ProfileNormalizer>();
builder.Services.AddSingleton<ProfileFactory>();
builder.Services.AddSingleton<ICallerResolver, CallerResolver>();
builder.Services.AddSingleton<PlayerService>();
builder.Services.AddSingleton<MasteringService>();

builder.Services.AddSingleton<AccountEndpoints>();
builder.Services.AddSingleton<EconomyEndpoints>();
builder.Services.AddSingleton<RouletteEndpoints>();
builder.Services.AddSingleton<SocialEndpoints>();
builder.Services.AddSingleton<ClanEndpoints>();
builder.Services.AddSingleton<PlayerServiceEndpoints>();
builder.Services.AddSingleton<MasteringEndpoints>();

builder.Services.AddSingleton<ILegacyRouter, LegacyRouter>();
builder.Services.AddSingleton<ILegacyRequestReader, LegacyRequestReader>();
builder.Services.AddSingleton<ILegacyResponseWriter, LegacyResponseWriter>();

var app = builder.Build();

app.Run(async context =>
{
    var reader = context.RequestServices.GetRequiredService<ILegacyRequestReader>();
    var writer = context.RequestServices.GetRequiredService<ILegacyResponseWriter>();
    var router = context.RequestServices.GetRequiredService<ILegacyRouter>();
    var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("CW.Server.Request");

    LegacyRequest request;

    try
    {
        request = await reader.ReadAsync(context, context.RequestAborted);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "unable to read request");
        await writer.WriteAsync(context, Reply.Fail(ex.GetType().Name), CancellationToken.None);
        return;
    }

    try
    {
        var result = router.Dispatch(request);

        logger.LogInformation(
            "{Ip,-15} {Method,-4} {Route}",
            request.ClientIp,
            request.Method,
            router.Describe(request));

        if (result.Raw is not null)
        {
            await writer.WriteRawAsync(context, result.Raw, context.RequestAborted);
        }
        else
        {
            await writer.WriteAsync(context, result.Payload, context.RequestAborted);
        }
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "{Route} failed", router.Describe(request));

        if (!context.Response.HasStarted)
        {
            await writer.WriteAsync(context, Reply.Fail(ex.GetType().Name), CancellationToken.None);
        }
    }
});

var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("CW.Server");
var catalog = app.Services.GetRequiredService<GameCatalog>();
var accounts = app.Services.GetRequiredService<IAccountRepository>();
var gameData = app.Services.GetRequiredService<IGameDataProvider>();

startupLogger.LogInformation("cw-server listening on http://{Host}:{Port}", options.Host, options.Port);
startupLogger.LogInformation("  data root       : {DataRoot}", dataRoot);
startupLogger.LogInformation("  static datasets : {Count} in backend_data/", gameData.BackendDatasetCount);
startupLogger.LogInformation("  content server  : {Cdn}", options.CdnHost);
startupLogger.LogInformation("  accounts        : {Count} known", accounts.Count);
startupLogger.LogInformation("  max level XP    : {MaxLevelXp}", catalog.MaxLevelXp);
startupLogger.LogInformation(
    "  advertised IP   : {Ip}",
    string.IsNullOrWhiteSpace(options.PublicIp) ? "(none - LAN hosts stay private)" : options.PublicIp);
startupLogger.LogInformation(
    "  new accounts    : {Mode}  next user_id={NextId}",
    options.FreshAccounts ? "FRESH (10000cr/100gp/5sp)" : "MAXED",
    accounts.NextUserId);

if (options.UnlockAll)
{
    startupLogger.LogInformation("  UNLOCK-ALL      : ON - all content served unlocked, stored progression untouched");
}

app.Run();
