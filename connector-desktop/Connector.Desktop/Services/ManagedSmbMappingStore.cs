using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace Connector.Desktop.Services;

/// <summary>Records assigned targets. Windows provides no mapping generation ID;
/// a matching target alone never proves exclusive ownership of a current mapping.</summary>
public sealed class ManagedSmbMappingStore
{
    private sealed record Mapping(string Drive, string Share);
    private sealed record OwnedMappings(int SchemaVersion, string WindowsIdentity, List<Mapping> Items);
    private readonly string _path;
    private readonly string _identity;
    private readonly Func<string, string?> _resolveTarget;

    public ManagedSmbMappingStore()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Structura Connector", "Network", "owned-smb.json"),
            WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("Не определён пользователь Windows."),
            ResolveWindowsTarget) { }

    public ManagedSmbMappingStore(string path, string identity, Func<string, string?> resolveTarget)
    {
        _path = Path.GetFullPath(path);
        _identity = identity;
        _resolveTarget = resolveTarget;
    }

    public bool MatchesRecordedTarget(string drive, string expectedShare)
    {
        var saved = Read();
        return saved is not null && saved.SchemaVersion == 1 && saved.WindowsIdentity == _identity &&
            saved.Items.Any(record => Same(record.Drive, drive) && Same(record.Share, expectedShare)) &&
            Same(_resolveTarget(drive), expectedShare);
    }

    public void Record(string drive, string share)
    {
        if (!Same(_resolveTarget(drive), share))
            throw new InvalidOperationException("Windows не подтвердил назначенное сетевое подключение.");
        var saved = Read();
        if (File.Exists(_path) && saved is null)
            throw new InvalidOperationException("Запись сетевых подключений требует восстановления.");
        if (saved is not null && (saved.SchemaVersion != 1 || saved.WindowsIdentity != _identity))
            throw new InvalidOperationException("Запись сетевых подключений принадлежит другому пользователю.");
        var items = saved?.Items.ToList() ?? [];
        items.RemoveAll(record => Same(record.Drive, drive));
        items.Add(new Mapping(drive, share));
        Save(new OwnedMappings(1, _identity, items));
    }

    private void Save(OwnedMappings mappings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(mappings));
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public void Forget(string drive)
    {
        var saved = Read();
        if (saved is not null && saved.SchemaVersion == 1 && saved.WindowsIdentity == _identity)
        {
            saved.Items.RemoveAll(record => Same(record.Drive, drive));
            Save(saved);
        }
    }

    private OwnedMappings? Read()
    {
        if (!File.Exists(_path)) return null;
        try
        {
            var info = new FileInfo(_path);
            if (info.Length > 8192) return null;
            var saved = JsonSerializer.Deserialize<OwnedMappings>(File.ReadAllText(_path));
            return saved is { Items: not null } && saved.Items.Count <= 26 && saved.Items.All(item => item is not null) ? saved : null;
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static bool Same(string? first, string second) =>
        string.Equals(first?.TrimEnd('\\'), second.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private static string? ResolveWindowsTarget(string drive)
    {
        var length = 1024;
        var target = new StringBuilder(length);
        var result = WNetGetConnection(drive, target, ref length);
        if (result == 234 && length <= 32768)
        {
            target = new StringBuilder(length);
            result = WNetGetConnection(drive, target, ref length);
        }
        if (result == 2250) return null;
        if (result is not (0 or 1201)) throw new Win32Exception(result);
        return target.ToString();
    }

    [DllImport("mpr.dll", EntryPoint = "WNetGetConnectionW", CharSet = CharSet.Unicode)]
    private static extern int WNetGetConnection(string localName, StringBuilder remoteName, ref int length);
}
