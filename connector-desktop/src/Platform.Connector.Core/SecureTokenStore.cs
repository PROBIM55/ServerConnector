using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Platform.Connector.Core;

/// <summary>
/// DPAPI-хранилище device-токена (CurrentUser). Единая реализация для
/// console-приложения и Desktop UI (раньше были две копии).
/// </summary>
public static class SecureTokenStore
{
    private const string PlainPrefix = "plain:";

    public static string GetDefaultPath()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Platform",
            "Connector",
            "secrets");
        return Path.Combine(root, "device-token.dat");
    }

    public static string ResolvePath(string? configuredPath)
    {
        return string.IsNullOrWhiteSpace(configuredPath) ? GetDefaultPath() : configuredPath;
    }

    public static bool TryLoad(string? configuredPath, out string token)
    {
        token = string.Empty;
        var path = ResolvePath(configuredPath);
        if (!File.Exists(path))
        {
            return false;
        }

        string raw;
        try
        {
            raw = File.ReadAllText(path, Encoding.UTF8).Trim();
        }
        catch
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        if (raw.StartsWith(PlainPrefix, StringComparison.Ordinal))
        {
            token = raw[PlainPrefix.Length..];
            return !string.IsNullOrWhiteSpace(token);
        }

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            var encrypted = Convert.FromBase64String(raw);
            var decrypted = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            token = Encoding.UTF8.GetString(decrypted);
            return !string.IsNullOrWhiteSpace(token);
        }
        catch
        {
            return false;
        }
    }

    public static void Save(string? configuredPath, string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new ArgumentException("Token is empty.", nameof(token));
        }

        var path = ResolvePath(configuredPath);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (OperatingSystem.IsWindows())
        {
            var bytes = Encoding.UTF8.GetBytes(token.Trim());
            var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            File.WriteAllText(path, Convert.ToBase64String(encrypted), Encoding.UTF8);
            return;
        }

        File.WriteAllText(path, PlainPrefix + token.Trim(), Encoding.UTF8);
    }
}
