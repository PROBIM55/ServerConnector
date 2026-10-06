using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Connector.Network;

namespace Connector.Network.Tests;

public sealed class WindowsNetBirdInstallationOwnershipGateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "netbird-gate-" + Guid.NewGuid().ToString("N"));
    private readonly string _scripts;
    private readonly string _preflight;
    private readonly string _verifier;
    private readonly string _identityLock;
    private readonly string _msi;
    private readonly string _cli;

    public WindowsNetBirdInstallationOwnershipGateTests()
    {
        _scripts = Path.Combine(_root, "scripts & pinned");
        _preflight = Path.Combine(_scripts, "netbird_windows_preflight.ps1");
        _verifier = Path.Combine(_scripts, "verify_netbird_installer.ps1");
        _identityLock = Path.Combine(_root, "infra", "netbird", "v0.79.0-windows-x64.lock.json");
        _msi = Path.Combine(_root, "pinned NetBird; package.msi");
        _cli = Path.Combine(_root, "NetBird", "netbird.exe");
        Directory.CreateDirectory(_scripts);
        Directory.CreateDirectory(Path.GetDirectoryName(_identityLock)!);
        Directory.CreateDirectory(Path.GetDirectoryName(_cli)!);
        File.WriteAllText(_preflight, "# pinned preflight", Encoding.UTF8);
        File.WriteAllText(_verifier, "# pinned verifier", Encoding.UTF8);
        File.WriteAllText(_identityLock, "{\"schemaVersion\":1}", Encoding.UTF8);
        File.WriteAllText(_msi, "pinned msi", Encoding.UTF8);
        File.WriteAllText(_cli, string.Empty, Encoding.UTF8);
    }

    [Fact]
    public async Task OwnedResult_UsesSystemPowerShellAndLiteralArgumentList()
    {
        var runner = new RecordingRunner(new NetBirdCommandResult(0, OwnedJson(_cli), string.Empty));
        var gate = CreateGate(runner);

        var result = await gate.CheckAsync(_cli, CancellationToken.None);

        Assert.True(result.IsTrusted);
        Assert.Equal("netbird_installation_owned", result.DiagnosticCode);
        var call = Assert.Single(runner.Calls);
        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
            call.Executable,
            ignoreCase: true);
        Assert.Equal(
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", _preflight, "-MsiPath", _msi],
            call.Arguments);
        Assert.Null(call.Environment);
        Assert.Equal(TimeSpan.FromSeconds(7), call.Timeout);
    }

    [Fact]
    public async Task ResultMustMatchEveryOwnershipInvariant()
    {
        var results = new[]
        {
            OwnedJson(_cli, schemaVersion: 2),
            OwnedJson(_cli, readOnly: false),
            OwnedJson(_cli, state: "foreign"),
            OwnedJson(_cli, decision: "blocked"),
            OwnedJson(_cli, safeToUse: false),
            OwnedJson(Path.Combine(_root, "other", "netbird.exe")),
        };

        foreach (var json in results)
        {
            var ownership = await CreateGate(new RecordingRunner(new NetBirdCommandResult(0, json, string.Empty)))
                .CheckAsync(_cli, CancellationToken.None);
            Assert.False(ownership.IsTrusted);
            Assert.Equal("netbird_preflight_not_owned", ownership.DiagnosticCode);
        }
    }

    [Theory]
    [InlineData(1, "{}")]
    [InlineData(0, "not-json")]
    [InlineData(0, "{}{}")]
    public async Task ProcessFailureOrInvalidJson_IsDenied(int exitCode, string output)
    {
        var ownership = await CreateGate(new RecordingRunner(new NetBirdCommandResult(exitCode, output, "failure")))
            .CheckAsync(_cli, CancellationToken.None);

        Assert.False(ownership.IsTrusted);
    }

    [Fact]
    public async Task ChangedPinnedInput_IsDeniedBeforePowerShellStarts()
    {
        var options = CreateOptions();
        File.AppendAllText(_verifier, "# tampered", Encoding.UTF8);
        var runner = new RecordingRunner(new NetBirdCommandResult(0, OwnedJson(_cli), string.Empty));
        var gate = new WindowsNetBirdInstallationOwnershipGate(options, runner, TrustedTestInputs());

        var ownership = await gate.CheckAsync(_cli, CancellationToken.None);

        Assert.False(ownership.IsTrusted);
        Assert.Equal("netbird_preflight_integrity_failed", ownership.DiagnosticCode);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task ProductionPolicy_DeniesUserWritableInputsBeforePowerShellStarts()
    {
        var runner = new RecordingRunner(new NetBirdCommandResult(0, OwnedJson(_cli), string.Empty));
        var gate = new WindowsNetBirdInstallationOwnershipGate(CreateOptions(), runner);

        var ownership = await gate.CheckAsync(_cli, CancellationToken.None);

        Assert.False(ownership.IsTrusted);
        Assert.Equal("netbird_preflight_path_untrusted", ownership.DiagnosticCode);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task MissingPinnedMsi_IsReportedBeforePathInspection()
    {
        File.Delete(_msi);
        var runner = new RecordingRunner(new NetBirdCommandResult(0, OwnedJson(_cli), string.Empty));

        var ownership = await CreateGate(runner).CheckAsync(_cli, CancellationToken.None);

        Assert.False(ownership.IsTrusted);
        Assert.Equal("netbird_preflight_msi_missing", ownership.DiagnosticCode);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task PathTrust_IsRecheckedAfterHashesBeforePowerShellStarts()
    {
        var trust = new SequencedPathTrustPolicy(true, false);
        var runner = new RecordingRunner(new NetBirdCommandResult(0, OwnedJson(_cli), string.Empty));
        var gate = new WindowsNetBirdInstallationOwnershipGate(CreateOptions(), runner, trust);

        var ownership = await gate.CheckAsync(_cli, CancellationToken.None);

        Assert.False(ownership.IsTrusted);
        Assert.Equal("netbird_preflight_path_untrusted", ownership.DiagnosticCode);
        Assert.Equal(2, trust.CallCount);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Timeout_IsDenied()
    {
        var ownership = await CreateGate(new RecordingRunner(new TimeoutException()))
            .CheckAsync(_cli, CancellationToken.None);

        Assert.False(ownership.IsTrusted);
        Assert.Equal("netbird_preflight_timeout", ownership.DiagnosticCode);
    }

    [Fact]
    public async Task Cancellation_IsDenied()
    {
        var ownership = await CreateGate(new RecordingRunner(new OperationCanceledException()))
            .CheckAsync(_cli, new CancellationToken(canceled: true));

        Assert.False(ownership.IsTrusted);
        Assert.Equal("netbird_preflight_cancelled", ownership.DiagnosticCode);
    }

    [Fact]
    public void OptionsRejectVerifierThatIsNotThePreflightDependency()
    {
        var options = CreateOptions(Path.Combine(_root, "other-verifier.ps1"));

        Assert.Throws<ArgumentException>(options.Validate);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private WindowsNetBirdInstallationOwnershipGate CreateGate(INetBirdCommandRunner runner) =>
        new(CreateOptions(), runner, TrustedTestInputs());

    private IWindowsNetBirdPathTrustPolicy TrustedTestInputs() =>
        new ExactPathTrustPolicy(_preflight, _verifier, _identityLock, _msi);

    private WindowsNetBirdInstallationOwnershipOptions CreateOptions(string? verifierScriptPath = null)
    {
        return new WindowsNetBirdInstallationOwnershipOptions
        {
            PreflightScriptPath = _preflight,
            PinnedMsiPath = _msi,
            VerifierScriptPath = verifierScriptPath ?? _verifier,
            IdentityLockPath = _identityLock,
            PreflightScriptSha256 = Hash(_preflight),
            VerifierScriptSha256 = Hash(_verifier),
            IdentityLockSha256 = Hash(_identityLock),
            Timeout = TimeSpan.FromSeconds(7),
        };
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static string OwnedJson(
        string cliPath,
        int schemaVersion = 1,
        bool readOnly = true,
        string state = "owned",
        string decision = "owned-ready",
        bool safeToUse = true) =>
        JsonSerializer.Serialize(new
        {
            schemaVersion,
            readOnly,
            state,
            decision,
            safeToUse,
            expected = new { cliPath },
        });

    private sealed class RecordingRunner : INetBirdCommandRunner
    {
        private readonly NetBirdCommandResult? _result;
        private readonly Exception? _exception;

        internal RecordingRunner(NetBirdCommandResult result) => _result = result;
        internal RecordingRunner(Exception exception) => _exception = exception;
        internal List<Call> Calls { get; } = [];

        public ValueTask<NetBirdCommandResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string?>? environment,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            Calls.Add(new(executable, arguments.ToArray(), environment, timeout));
            return _exception is null
                ? ValueTask.FromResult(_result!)
                : ValueTask.FromException<NetBirdCommandResult>(_exception);
        }
    }

    private sealed record Call(
        string Executable,
        IReadOnlyList<string> Arguments,
        IReadOnlyDictionary<string, string?>? Environment,
        TimeSpan Timeout);

    private sealed class ExactPathTrustPolicy(params string[] trustedPaths) : IWindowsNetBirdPathTrustPolicy
    {
        private readonly HashSet<string> _trustedPaths = trustedPaths
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        public bool AreTrusted(IReadOnlyCollection<string> paths) =>
            paths.Count == _trustedPaths.Count &&
            paths.All(path => _trustedPaths.Contains(Path.GetFullPath(path)));
    }

    private sealed class SequencedPathTrustPolicy(params bool[] decisions) : IWindowsNetBirdPathTrustPolicy
    {
        internal int CallCount { get; private set; }

        public bool AreTrusted(IReadOnlyCollection<string> paths)
        {
            var index = CallCount++;
            return index < decisions.Length && decisions[index];
        }
    }

}
