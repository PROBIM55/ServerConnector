using Connector.Desktop.Models;
using Connector.Desktop.Services;
using Xunit;

namespace Connector.Desktop.Tests;

public sealed class AtomicSettingsSaveTests
{
    [Fact]
    public void FailedReplacementPreservesCompletePreviouslySavedSettings()
    {
        var path = Path.Combine(Path.GetTempPath(), "connector-save-" + Guid.NewGuid().ToString("N"), "settings.json");
        var service = new SettingsService(path);
        service.Save(new AppSettings { DeviceId = "preserved-device", TeklaStandardLocalPath = @"C:\Company\Preserved", TokenCipherBase64 = "fixture-cipher" });
        var original = File.ReadAllBytes(path);
        using var competingReader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var failure = Record.Exception(() => service.Save(new AppSettings { DeviceId = "replacement" }));
        Assert.True(failure is IOException or UnauthorizedAccessException, failure?.ToString() ?? "Replacement unexpectedly succeeded.");
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal("preserved-device", service.Load().DeviceId);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }
}
