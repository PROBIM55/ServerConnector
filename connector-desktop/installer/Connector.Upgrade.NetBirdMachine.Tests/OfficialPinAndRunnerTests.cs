using Connector.Upgrade.NetBirdMachine;
using Connector.Upgrade.WindowsMsi;

namespace Connector.Upgrade.NetBirdMachine.Tests;

public sealed class OfficialPinAndRunnerTests
{
    [Fact]
    public void LoadsOfficial079LockAsSinglePackageTruth()
    {
        var pin = OfficialNetBirdPackagePin.LoadEmbedded();

        Assert.Equal("0.79.0", pin.Version);
        Assert.Equal("netbird_installer_0.79.0_windows_amd64.msi", pin.InstallerName);
        Assert.Equal(23543808, pin.InstallerBytes);
        Assert.Equal("50F822C0F5F6E54E7618096CDD36B63BF4B169E125C9DA06052FD4FD96115210", pin.InstallerSha256);
        Assert.Equal("7B41FCCAFCB794720FE07D381F9CBDF18AB5900F", pin.SignerThumbprint);
    }

    [Fact]
    public async Task WindowsRunnerUsesFixedQuietInstallArgumentsWithoutShellElevation()
    {
        var process = new RecordingProcessRunner();
        var runner = new WindowsNetBirdMsiMutationRunner(
            process,
            TimeSpan.FromMinutes(2),
            "C:\\Windows\\System32\\msiexec.exe");
        var msi = "C:\\ProgramData\\StructuraConnectorInstaller\\NetBird\\netbird.msi";

        await runner.InstallAsync(msi, CancellationToken.None);

        Assert.Equal("C:\\Windows\\System32\\msiexec.exe", process.Request!.FileName);
        Assert.Equal(["/i", msi, "/qn", "/norestart"], process.Request.Arguments);
    }

    [Fact]
    public async Task WindowsRunnerUninstallsOnlyTheExactProductCode()
    {
        var process = new RecordingProcessRunner();
        var runner = new WindowsNetBirdMsiMutationRunner(
            process,
            TimeSpan.FromMinutes(2),
            "C:\\Windows\\System32\\msiexec.exe");
        var productCode = Guid.Parse("463D0C9D-ED41-451E-A44F-937932C8B267");

        await runner.UninstallExactAsync(productCode, CancellationToken.None);

        Assert.Equal(
            ["/x", "{463D0C9D-ED41-451E-A44F-937932C8B267}", "/qn", "/norestart"],
            process.Request!.Arguments);
    }

    private sealed class RecordingProcessRunner : IWindowsMsiProcessRunner
    {
        internal WindowsMsiProcessRequest? Request { get; private set; }

        public ValueTask<WindowsMsiProcessResult> RunAsync(
            WindowsMsiProcessRequest request,
            CancellationToken cancellationToken)
        {
            Request = request;
            return ValueTask.FromResult(new WindowsMsiProcessResult(WindowsMsiProcessCompletion.Exited, 0));
        }
    }
}
