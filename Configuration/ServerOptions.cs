namespace CW.Server.Configuration;

public sealed class ServerOptions
{
    public const string SectionName = "Server";

    public string Host { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 8099;

    public string CdnHost { get; set; } = "cdn-01.contractwarsgame.com";

    public bool MaxProfile { get; set; } = true;

    public bool FreshAccounts { get; set; } = true;

    public bool UnlockAll { get; set; }

    public bool HttpDebug { get; set; }

    public string? PublicIp { get; set; }

    public string? DataRoot { get; set; }

    public int SuitSlots { get; set; } = 6;

    public int TransactionLimit { get; set; } = 30;

    public int RatingPageSize { get; set; } = 100;

    public int ClanTopSize { get; set; } = 50;
}
