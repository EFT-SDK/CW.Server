namespace CW.Server.Configuration;

public static class ServerDataLocator
{
    public const string DirectoryName = "CW.Server_Data";
    public const string EnvironmentVariable = "CW_SERVER_DATA";

    public static string Resolve(string? configuredPath, string baseDirectory)
    {
        var explicitPath = FirstNonEmpty(
            configuredPath,
            Environment.GetEnvironmentVariable(EnvironmentVariable));

        if (explicitPath is not null)
        {
            return Path.GetFullPath(explicitPath);
        }

        var discovered = ProbeUpwards(baseDirectory);
        if (discovered is not null)
        {
            return discovered;
        }

        return Path.Combine(TrimTrailingSeparator(baseDirectory), DirectoryName);
    }

    private static string? ProbeUpwards(string baseDirectory)
    {
        var current = new DirectoryInfo(TrimTrailingSeparator(baseDirectory));
        string? firstNameMatch = null;

        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, DirectoryName);

            if (Directory.Exists(candidate))
            {
                if (IsPopulated(candidate))
                {
                    return candidate;
                }

                firstNameMatch ??= candidate;
            }

            current = current.Parent;
        }

        return firstNameMatch;
    }

    private static bool IsPopulated(string candidate)
    {
        return File.Exists(Path.Combine(candidate, "server.json"))
               || HasFiles(Path.Combine(candidate, "backend_data"))
               || HasFiles(Path.Combine(candidate, "templates"));
    }

    private static bool HasFiles(string directory)
    {
        return Directory.Exists(directory)
               && Directory.EnumerateFiles(directory, "*.json").Any();
    }

    private static string TrimTrailingSeparator(string path)
    {
        return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string? FirstNonEmpty(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate.Trim();
            }
        }

        return null;
    }
}
