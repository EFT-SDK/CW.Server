namespace CW.Server.Configuration;

public sealed class ServerPaths
{
    public ServerPaths(string dataRoot)
    {
        DataRoot = dataRoot;
        BackendData = Path.Combine(dataRoot, "backend_data");
        Templates = Path.Combine(dataRoot, "templates");
        State = Path.Combine(dataRoot, "server_state");
    }

    public string DataRoot { get; }

    public string BackendData { get; }

    public string Templates { get; }

    public string State { get; }

    public string BackendFile(string name) => Path.Combine(BackendData, name);

    public string TemplateFile(string name) => Path.Combine(Templates, name);

    public string StateFile(string name) => Path.Combine(State, name);

    public void EnsureCreated()
    {
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(BackendData);
        Directory.CreateDirectory(Templates);
        Directory.CreateDirectory(State);
    }
}
