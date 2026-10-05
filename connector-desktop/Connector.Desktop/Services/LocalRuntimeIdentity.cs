using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace Connector.Desktop.Services;

/// <summary>A persistent local queue identity bound to the Windows user and this computer.</summary>
public static class LocalRuntimeIdentity
{
    private sealed record Identity(int SchemaVersion, string Binding, string Cipher);

    public static string LoadOrCreate(string runtimeRoot)
    {
        using var registry = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
        var machine = registry?.GetValue("MachineGuid") as string;
        var user = WindowsIdentity.GetCurrent().User?.Value;
        if (string.IsNullOrWhiteSpace(machine) || string.IsNullOrWhiteSpace(user))
            throw new InvalidOperationException("Не удалось подтвердить владельца локальной очереди.");
        var binding = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(machine + ":" + user)));
        return LoadOrCreate(runtimeRoot, binding);
    }

    internal static string LoadOrCreate(string runtimeRoot, string binding)
    {
        Directory.CreateDirectory(runtimeRoot);
        var path = Path.Combine(runtimeRoot, "local-identity.json");
        if (File.Exists(path)) return Read(path, binding);
        var id = "local-" + Guid.NewGuid().ToString("N");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Identity(1, binding, SettingsService.EncryptToken(id))));
            try { File.Move(temporary, path); }
            catch (IOException) when (File.Exists(path)) { return Read(path, binding); }
            return id;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string Read(string path, string binding)
    {
        try
        {
            if (new FileInfo(path).Length > 8192) throw new InvalidDataException();
            var saved = JsonSerializer.Deserialize<Identity>(File.ReadAllText(path));
            if (saved is null || saved.SchemaVersion != 1 || saved.Binding != binding) throw new InvalidDataException();
            var id = SettingsService.DecryptToken(saved.Cipher);
            if (!id.StartsWith("local-", StringComparison.Ordinal) || !Guid.TryParseExact(id[6..], "N", out _))
                throw new InvalidDataException();
            return id;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or FormatException or CryptographicException)
        {
            // Never overwrite foreign or damaged identity/state with a new owner.
            throw new InvalidOperationException("Локальная очередь принадлежит другому устройству или требует восстановления.");
        }
    }
}
