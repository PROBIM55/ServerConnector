using Connector.Desktop.Features.Vpn;
using Connector.Desktop.Mvvm;
using Connector.Desktop.Services;
using Xunit;

namespace Connector.Desktop.Tests;

public sealed class VpnConnectionTests
{
    private static readonly VpnContext Context = new(true, @"\\62.113.36.107\BIM_Models", "10.77.123.1", "test-config", "", "");

    [Fact]
    public async Task RunningServiceWithoutReachableServerDoesNotReportSuccessOrOpenShare()
    {
        var service = new FakeVpn { Reachable = false };
        var vm = new VpnViewModel(service);
        var opened = false;
        vm.OpenShareHandler = _ => opened = true;
        vm.SetContext(Context);
        var result = await vm.EnsureEnabledAsync(false, true);
        Assert.False(result.IsSuccess);
        Assert.False(opened);
        Assert.Contains("недоступен", vm.StatusLine);
    }

    [Fact]
    public async Task ManualReconnectRequestsRestartWhileAutomaticEnsureDoesNot()
    {
        var service = new FakeVpn { Reachable = true };
        var vm = new VpnViewModel(service);
        vm.SetContext(Context);
        await vm.EnsureEnabledAsync(false, false);
        Assert.False(service.RestartRequested);
        await ((AsyncRelayCommand)vm.EnableCommand).ExecuteAsync();
        Assert.True(service.RestartRequested);
        Assert.Contains("SMB-порт сервера доступен", vm.StatusLine);
    }

    [Fact]
    public async Task CancelledRestartDoesNotOpenShareOrClaimSuccessfulOperation()
    {
        var service = new FakeVpn { Reachable = false, Cancel = true };
        var vm = new VpnViewModel(service);
        var opened = false;
        vm.OpenShareHandler = _ => opened = true;
        vm.SetContext(Context);
        await ((AsyncRelayCommand)vm.EnableCommand).ExecuteAsync();
        Assert.Equal(OperationOutcome.Cancelled, vm.LastOperationOutcome);
        Assert.False(opened);
    }

    [Fact]
    public async Task DelayedProbeCannotRestoreStatusOfPreviousContext()
    {
        var pending = new TaskCompletionSource<bool>();
        var service = new FakeVpn { Pending = pending.Task };
        var vm = new VpnViewModel(service);
        vm.SetContext(Context);
        vm.SetContext(VpnContext.Empty);
        pending.SetResult(true);
        await vm.RefreshConnectionAsync();
        Assert.Contains("не настроен", vm.StatusLine);
    }

    private sealed class FakeVpn : IVpnProvisioningService
    {
        public bool Reachable { get; init; }
        public bool Cancel { get; init; }
        public bool RestartRequested { get; private set; }
        public Task<bool>? Pending { get; init; }
        public bool BundledClientPresent => true;
        public string LogFilePath => "unused";
        public bool IsTunnelInstalled(string tunnel) => false;
        public bool IsTunnelRunning(string tunnel) => true;
        public Task<bool> CanReachServerAsync(string serverIp) => Pending ?? Task.FromResult(Reachable);
        public VpnResult Enable(string config, string tunnel, bool restartExisting = false)
        {
            RestartRequested = restartExisting;
            return Cancel ? VpnResult.Cancelled("cancelled") : VpnResult.Success("running");
        }
        public VpnResult Disable(string tunnel) => VpnResult.Success("disabled");
    }
}
