namespace Connector.Upgrade.UserState;

public static class LegacyUserStateRoots
{
    public static IReadOnlyList<UserStateRoot> FromLocalApplicationData(string localApplicationData)
    {
        if (string.IsNullOrWhiteSpace(localApplicationData))
        {
            throw new ArgumentException("Local application data root is required.", nameof(localApplicationData));
        }

        var root = Path.GetFullPath(localApplicationData);
        return
        [
            new UserStateRoot(
                LegacyUserStateRootIds.Structura,
                Path.Combine(root, "ConnectorAgentDesktop")),
            new UserStateRoot(
                LegacyUserStateRootIds.Platform,
                Path.Combine(root, "Platform", "Connector")),
            new UserStateRoot(
                LegacyUserStateRootIds.UnifiedPlatform,
                Path.Combine(root, "Structura Connector", "Platform"))
        ];
    }

    public static IReadOnlyList<UserStateRoot> ForCurrentUser()
    {
        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localApplicationData))
        {
            throw new UserStateSnapshotException("LocalApplicationData is unavailable for the current user.");
        }

        return FromLocalApplicationData(localApplicationData);
    }
}
