using System.Text.Json;
using Connector.Desktop.Models;
using Connector.Desktop.Services;
using Xunit;

namespace Connector.Desktop.Tests;

public sealed class ParallelSettingsTests
{
    [Fact]
    public void Enabling_and_disabling_new_autostart_preserve_the_old_launch_entry()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = @"Software\StructuraConnectorTests\" + Guid.NewGuid().ToString("N");
        try
        {
            using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(path))
                key.SetValue("ConnectorAgentDesktop", "old-launcher.exe");
            var service = new AutoStartService(path, () => "new-launcher.exe");
            service.SetEnabled(true);
            Assert.True(service.IsEnabled());
            service.SetEnabled(false);
            Assert.False(service.IsEnabled());
            using var actual = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(path);
            Assert.Equal("old-launcher.exe", actual!.GetValue("ConnectorAgentDesktop"));
        }
        finally { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false); }
    }

    [Fact]
    public void Import_preferences_and_save_new_profile_leave_legacy_bytes_and_credentials_unchanged()
    {
        var root = Path.Combine(Path.GetTempPath(), "connector-parallel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var oldPath = Path.Combine(root, "old.json");
            var newPath = Path.Combine(root, "new", "settings.json");
            var old = new AppSettings { TokenCipherBase64 = "old-envelope", SmbLogin = "old-user",
                SmbPasswordCipherBase64 = "old-password-envelope", VpnEnabled = true,
                VpnConfigCipherBase64 = "old-vpn-envelope", TeklaStandardLocalPath = @"C:\Company\TeklaFirm",
                DeviceId = "", HeartbeatSeconds = 0, IsSystemAdmin = true, IsFirmAdmin = true };
            File.WriteAllText(oldPath, JsonSerializer.Serialize(old));
            var before = File.ReadAllBytes(oldPath);
            var service = new SettingsService(newPath, oldPath);
            var imported = service.Load();
            Assert.Equal(old.TeklaStandardLocalPath, imported.TeklaStandardLocalPath);
            Assert.Empty(imported.TokenCipherBase64);
            Assert.Empty(imported.SmbPasswordCipherBase64);
            Assert.False(imported.VpnEnabled);
            Assert.False(imported.IsSystemAdmin);
            Assert.False(imported.IsFirmAdmin);
            Assert.False(string.IsNullOrWhiteSpace(imported.DeviceId));
            Assert.Equal(60, imported.HeartbeatSeconds);
            imported.TokenCipherBase64 = "new-envelope";
            service.Save(imported);
            Assert.Equal(before, File.ReadAllBytes(oldPath));
            Assert.Equal("new-envelope", service.Load().TokenCipherBase64);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
