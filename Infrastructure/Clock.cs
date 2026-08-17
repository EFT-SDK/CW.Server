namespace CW.Server.Infrastructure;

public interface IClock
{
    DateTimeOffset Now { get; }

    long UnixSeconds { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset Now => DateTimeOffset.Now;

    public long UnixSeconds => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}
