namespace CW.Server.Infrastructure;

public sealed class StateLock
{
    private readonly object _gate = new();

    public Scope Enter()
    {
        Monitor.Enter(_gate);
        return new Scope(_gate);
    }

    public readonly struct Scope : IDisposable
    {
        private readonly object _gate;

        internal Scope(object gate) => _gate = gate;

        public void Dispose() => Monitor.Exit(_gate);
    }
}
