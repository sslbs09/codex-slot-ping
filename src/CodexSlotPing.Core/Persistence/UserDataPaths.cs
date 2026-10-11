namespace CodexSlotPing.Core.Persistence;

public sealed record UserDataPaths
{
    public UserDataPaths(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        RootDirectory = Path.GetFullPath(rootDirectory);
    }

    public string RootDirectory { get; }

    public string ConfigPath => Path.Combine(RootDirectory, "config.json");

    public string StatePath => Path.Combine(RootDirectory, "state.json");

    public string LogsDirectory => Path.Combine(RootDirectory, "logs");

    public static UserDataPaths ForCurrentUser()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        ArgumentException.ThrowIfNullOrWhiteSpace(localAppData);
        return new UserDataPaths(Path.Combine(localAppData, "CodexSlotPing"));
    }
}
