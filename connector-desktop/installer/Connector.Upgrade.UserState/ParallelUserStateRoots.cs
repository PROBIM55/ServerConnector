namespace Connector.Upgrade.UserState;

/// <summary>Only the new application's profile may be rolled back during coexistence.</summary>
public static class ParallelUserStateRoots
{
    public const string Unified = "unified-connector";

    public static IReadOnlyList<UserStateRoot> FromLocalApplicationData(string localApplicationData)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localApplicationData);
        return [new UserStateRoot(Unified, Path.Combine(Path.GetFullPath(localApplicationData), "Structura Unified Connector"))];
    }

    public static IReadOnlyList<UserStateRoot> ForCurrentUser() =>
        FromLocalApplicationData(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
}
