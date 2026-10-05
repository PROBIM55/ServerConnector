using Connector.Network;
using System.Diagnostics;

namespace Connector.Network.Tests;

public sealed class NetBirdCliClientTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "connector-network-" + Guid.NewGuid().ToString("N"));
    private readonly string _executable;

    public NetBirdCliClientTests()
    {
        Directory.CreateDirectory(_root);
        _executable = Path.Combine(_root, "netbird.exe");
        File.WriteAllText(_executable, string.Empty);
    }

    [Fact]
    public async Task Connect_PassesSetupKeyOnlyThroughChildEnvironment_AndRequiresReadyStatus()
    {
        var runner = new QueueRunner(
            new(0, "{\"daemonStatus\":\"Idle\"}", ""),
            new(0, "", ""),
            new(0, "", ""),
            new(0, "{\"daemonStatus\":\"Connected\",\"management\":{\"connected\":true,\"url\":\"https://vpn.example.test\"},\"signal\":{\"connected\":true},\"netbirdIp\":\"100.90.0.22/16\",\"publicKey\":\"AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA=\"}", ""));
        var client = CreateClient(runner);

        var result = await client.ConnectAsync(new NetworkOverlayBootstrap(new Uri("https://vpn.example.test"), "secret-setup-key"));

        Assert.Equal(NetworkServiceStatus.Ready, result.Status);
        Assert.Equal(["status", "--json"], runner.Calls[0].Arguments);
        Assert.DoesNotContain("secret-setup-key", runner.Calls[1].Arguments);
        Assert.Equal("secret-setup-key", runner.Calls[1].Environment!["NB_SETUP_KEY"]);
        Assert.Single(runner.Calls[1].Environment!);
        Assert.Equal("100.90.0.22", Assert.Single(result.AssignedInternalAddresses).ToString());
        Assert.Equal("https://vpn.example.test/", result.ManagementUri!.AbsoluteUri);
    }

    [Fact]
    public async Task InvalidStatusJson_ProducesTypedUnknownState()
    {
        var runner = new QueueRunner(new(1, "", "not ready"), new(0, "not-json", ""));
        var client = CreateClient(runner);

        var result = await client.GetStatusAsync();

        Assert.Equal(NetworkServiceStatus.Unknown, result.Status);
        Assert.Empty(result.AssignedInternalAddresses);
    }

    [Fact]
    public async Task Connect_RejectsUntrustedInstallation_BeforeUp()
    {
        var runner = new QueueRunner();
        var client = new NetBirdCliClient(
            new NetBirdCliOptions { ExecutablePath = _executable },
            runner,
            new FixedOwnershipGate(false));

        var error = await Assert.ThrowsAsync<NetworkGateException>(() => client.ConnectAsync(
            new NetworkOverlayBootstrap(new Uri("https://vpn.example.test"), "secret-setup-key")).AsTask());

        Assert.Equal("netbird_installation_untrusted", error.Code);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task PublicConstructorWithoutVerifiedOwnershipOptions_DefaultsToDeny()
    {
        var client = new NetBirdCliClient(new NetBirdCliOptions { ExecutablePath = _executable });

        var status = await client.GetStatusAsync();

        Assert.Equal(NetworkServiceStatus.Unknown, status.Status);
        Assert.Equal("netbird_installation_untrusted", status.DiagnosticCode);
    }

    [Fact]
    public async Task Connect_RejectsReadyForeignManagementUrl()
    {
        var runner = new QueueRunner(
            new NetBirdCommandResult(0, "{\"daemonStatus\":\"Connected\",\"management\":{\"connected\":true,\"url\":\"https://foreign.example.test\"}}", ""));
        var client = CreateClient(runner);

        var error = await Assert.ThrowsAsync<NetworkGateException>(() => client.ConnectAsync(
            new NetworkOverlayBootstrap(new Uri("https://vpn.example.test"), "secret-setup-key")).AsTask());

        Assert.Equal("netbird_management_mismatch", error.Code);
        Assert.Single(runner.Calls);
        Assert.Equal(["status", "--json"], runner.Calls[0].Arguments);
    }

    [Fact]
    public async Task Connect_RejectsUnreadableDaemonStatus_BeforeUp()
    {
        var runner = new QueueRunner(new NetBirdCommandResult(1, "", "daemon unavailable"));
        var client = CreateClient(runner);

        var error = await Assert.ThrowsAsync<NetworkGateException>(() => client.ConnectAsync(
            new NetworkOverlayBootstrap(new Uri("https://vpn.example.test"), "secret-setup-key")).AsTask());

        Assert.Equal("netbird_pre_up_status_unavailable", error.Code);
        Assert.Single(runner.Calls);
        Assert.Equal(["status", "--json"], runner.Calls[0].Arguments);
    }

    [Fact]
    public void CommandRunner_ClearsInheritedNetBirdVariables()
    {
        var startInfo = new ProcessStartInfo();
        startInfo.Environment["NB_MANAGEMENT_URL"] = "https://foreign.example.test";
        startInfo.Environment["NB_DAEMON_ADDR"] = "tcp://foreign.example.test:33073";
        startInfo.Environment["NB_SERVICE"] = "foreign-service";

        NetBirdCommandRunner.ApplyEnvironment(startInfo, new Dictionary<string, string?>
        {
            ["NB_SETUP_KEY"] = "secret-setup-key",
        });

        Assert.DoesNotContain(startInfo.Environment.Keys, key => key.StartsWith("NB_", StringComparison.OrdinalIgnoreCase) && key != "NB_SETUP_KEY");
        Assert.Equal("secret-setup-key", startInfo.Environment["NB_SETUP_KEY"]);
    }

    [Fact]
    public async Task Status_WithNoValidPublicKey_IsNotReady()
    {
        var runner = new QueueRunner(
            new(0, "", ""),
            new(0, "{\"daemonStatus\":\"Connected\",\"management\":{\"connected\":true,\"url\":\"https://vpn.example.test\"},\"signal\":{\"connected\":true},\"netbirdIp\":\"100.90.0.22/16\"}", ""));
        var client = CreateClient(runner);

        var result = await client.GetStatusAsync();

        Assert.NotEqual(NetworkServiceStatus.Ready, result.Status);
    }

    [Fact]
    public async Task LocalIdentity_ReadyStatus_ReturnsCanonicalKeyAndObservedIdentity()
    {
        var runner = new QueueRunner(
            new(0, "", ""),
            new(0, "{\"daemonStatus\":\"Connected\",\"management\":{\"connected\":true,\"url\":\"https://vpn.example.test\"},\"signal\":{\"connected\":true},\"netbirdIp\":\"100.90.0.22/16\",\"publicKey\":\"AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA=\"}", ""));
        var client = CreateClient(runner);

        var result = await client.GetLocalIdentityAsync(new Uri("https://vpn.example.test"));

        Assert.Equal(NetBirdLocalIdentityStatus.Available, result.Status);
        var identity = Assert.IsType<NetBirdLocalIdentity>(result.Identity);
        Assert.Equal("AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA=", identity.WireGuardPublicKey);
        Assert.Equal("https://vpn.example.test/", identity.ManagementUri.AbsoluteUri);
        Assert.Equal("100.90.0.22", Assert.Single(identity.AssignedInternalAddresses).ToString());
        Assert.Equal(["status", "--check", "startup"], runner.Calls[0].Arguments);
        Assert.Equal(["status", "--json"], runner.Calls[1].Arguments);
    }

    [Theory]
    [InlineData("AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA", "netbird_degraded")]
    [InlineData("AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyB=", "netbird_identity_invalid")]
    public async Task LocalIdentity_MalformedOrNoncanonicalKey_IsUnavailable(string publicKey, string diagnosticCode)
    {
        var runner = new QueueRunner(
            new(0, "", ""),
            new(0, "{\"daemonStatus\":\"Connected\",\"management\":{\"connected\":true,\"url\":\"https://vpn.example.test\"},\"signal\":{\"connected\":true},\"netbirdIp\":\"100.90.0.22/16\",\"publicKey\":\"" + publicKey + "\"}", ""));
        var client = CreateClient(runner);

        var result = await client.GetLocalIdentityAsync(new Uri("https://vpn.example.test"));

        Assert.Equal(NetBirdLocalIdentityStatus.Unavailable, result.Status);
        Assert.Null(result.Identity);
        Assert.Equal(diagnosticCode, result.DiagnosticCode);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    public async Task LocalIdentity_NonObjectStatusJson_IsUnavailable(string statusJson)
    {
        var runner = new QueueRunner(new(0, "", ""), new(0, statusJson, ""));
        var client = CreateClient(runner);

        var result = await client.GetLocalIdentityAsync(new Uri("https://vpn.example.test"));

        Assert.Equal(NetBirdLocalIdentityStatus.Unavailable, result.Status);
        Assert.Null(result.Identity);
        Assert.Equal("netbird_status_invalid", result.DiagnosticCode);
    }

    [Fact]
    public async Task LocalIdentity_RejectsUntrustedInstallation()
    {
        var client = new NetBirdCliClient(
            new NetBirdCliOptions { ExecutablePath = _executable },
            new QueueRunner(),
            new FixedOwnershipGate(false));

        var result = await client.GetLocalIdentityAsync(new Uri("https://vpn.example.test"));

        Assert.Equal(NetBirdLocalIdentityStatus.Unavailable, result.Status);
        Assert.Null(result.Identity);
        Assert.Equal("netbird_installation_untrusted", result.DiagnosticCode);
    }

    [Fact]
    public async Task LocalIdentity_RejectsWrongManagementUri()
    {
        var runner = new QueueRunner(
            new(0, "", ""),
            new(0, "{\"daemonStatus\":\"Connected\",\"management\":{\"connected\":true,\"url\":\"https://foreign.example.test\"},\"signal\":{\"connected\":true},\"netbirdIp\":\"100.90.0.22/16\",\"publicKey\":\"AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA=\"}", ""));
        var client = CreateClient(runner);

        var result = await client.GetLocalIdentityAsync(new Uri("https://vpn.example.test"));

        Assert.Equal(NetBirdLocalIdentityStatus.Unavailable, result.Status);
        Assert.Null(result.Identity);
        Assert.Equal("netbird_management_mismatch", result.DiagnosticCode);
    }

    [Fact]
    public async Task LocalIdentity_RejectsDegradedDaemon()
    {
        var runner = new QueueRunner(
            new(0, "", ""),
            new(0, "{\"daemonStatus\":\"Connecting\",\"management\":{\"connected\":true,\"url\":\"https://vpn.example.test\"},\"signal\":{\"connected\":false},\"netbirdIp\":\"100.90.0.22/16\",\"publicKey\":\"AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA=\"}", ""));
        var client = CreateClient(runner);

        var result = await client.GetLocalIdentityAsync(new Uri("https://vpn.example.test"));

        Assert.Equal(NetBirdLocalIdentityStatus.Unavailable, result.Status);
        Assert.Null(result.Identity);
        Assert.Equal("netbird_degraded", result.DiagnosticCode);
    }

    [Fact]
    public async Task LocalIdentity_PreservesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var client = new NetBirdCliClient(
            new NetBirdCliOptions { ExecutablePath = _executable },
            new QueueRunner(),
            new CancelledOwnershipGate());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetLocalIdentityAsync(
            new Uri("https://vpn.example.test"), cancellation.Token).AsTask());
    }

    [Fact]
    public async Task CommandRunner_CallerCancellationTerminatesOwnedChild()
    {
        var powerShell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        var script = Path.Combine(_root, "sleep.ps1");
        var pidFile = Path.Combine(_root, "child.pid");
        File.WriteAllText(script,
            "$PID | Set-Content -LiteralPath '" + pidFile.Replace("'", "''", StringComparison.Ordinal) +
            "' -NoNewline\nStart-Sleep -Seconds 30\n");
        using var cancellation = new CancellationTokenSource();
        var runner = new NetBirdCommandRunner();
        var running = runner.RunAsync(powerShell, ["-NoProfile", "-File", script], null,
            TimeSpan.FromSeconds(40), cancellation.Token).AsTask();

        Assert.True(SpinWait.SpinUntil(() => File.Exists(pidFile), TimeSpan.FromSeconds(5)),
            "The owned child never started.");
        var childPid = int.Parse(File.ReadAllText(pidFile), System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
            using var child = Process.GetProcessById(childPid);
            Assert.True(child.WaitForExit(5000), "Cancellation left the owned child running.");
        }
        catch (ArgumentException)
        {
            // Process.GetProcessById throws if the child already exited, which is the expected outcome.
        }
        finally
        {
            try
            {
                using var child = Process.GetProcessById(childPid);
                if (!child.HasExited) child.Kill(entireProcessTree: true);
            }
            catch (ArgumentException) { }
        }
    }

    public void Dispose() => Directory.Delete(_root, true);

    private NetBirdCliClient CreateClient(QueueRunner runner) => new(
        new NetBirdCliOptions { ExecutablePath = _executable },
        runner,
        new FixedOwnershipGate(true));

    private sealed class QueueRunner(params NetBirdCommandResult[] results) : INetBirdCommandRunner
    {
        private readonly Queue<NetBirdCommandResult> _results = new(results);
        public List<Call> Calls { get; } = [];
        public ValueTask<NetBirdCommandResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string?>? environment,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            Calls.Add(new Call(arguments.ToArray(), environment));
            return ValueTask.FromResult(_results.Dequeue());
        }
    }

    private sealed record Call(IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string?>? Environment);

    private sealed class FixedOwnershipGate(bool trusted) : INetBirdInstallationOwnershipGate
    {
        public ValueTask<NetBirdInstallationOwnership> CheckAsync(string executablePath, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new NetBirdInstallationOwnership(trusted, "netbird_installation_untrusted"));
    }

    private sealed class CancelledOwnershipGate : INetBirdInstallationOwnershipGate
    {
        public ValueTask<NetBirdInstallationOwnership> CheckAsync(string executablePath, CancellationToken cancellationToken) =>
            ValueTask.FromCanceled<NetBirdInstallationOwnership>(cancellationToken);
    }
}
