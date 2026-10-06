// Connector-side reader для %LOCALAPPDATA%\Platform\Bridge\token.dat.
// Файл пишется Bridge.Desktop через ProtectedData.Protect (DPAPI, CurrentUser
// scope) — тот же Windows-пользователь читает через ProtectedData.Unprotect.
// См. Platform.Bridge.Desktop.Tekla/Auth/BridgeTokenStore.cs.
//
// Этот reader живёт в connector'е (net8.0); там нет dependency на bridge-desktop
// (net48). Файл-формат — sole contract между ними.

using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Platform.Cad.Tekla.Adapter;

[SupportedOSPlatform("windows")]
public static class BridgeTokenReader
{
    public static string DefaultPath
    {
        get
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "Platform", "Bridge", "token.dat");
        }
    }

    /// <summary>
    /// Возвращает токен или null если файла нет / не удалось расшифровать.
    /// Не бросает — caller проверит null и вернёт BRIDGE_TOKEN_UNAVAILABLE.
    /// </summary>
    public static string? TryLoad(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (!File.Exists(path)) return null;
            var enc = File.ReadAllBytes(path);
            if (enc.Length == 0) return null;
            var dec = ProtectedData.Unprotect(enc, optionalEntropy: null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(dec);
        }
        catch
        {
            return null;
        }
    }
}
