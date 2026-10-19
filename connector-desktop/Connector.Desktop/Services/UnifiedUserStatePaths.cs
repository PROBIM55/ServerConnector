using System.IO;

namespace Connector.Desktop.Services;

public static class UnifiedUserStatePaths
{
    public static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Platform Connector");
    public static string Structura => Path.Combine(Root, "Structura");
    public static string LegacySettings => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ConnectorAgentDesktop", "settings.json");
}
