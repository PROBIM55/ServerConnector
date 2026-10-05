using Connector.Upgrade.Bootstrapper;
using Connector.Upgrade.Core;
using Connector.Upgrade.WindowsUserJournal;

namespace Connector.Upgrade.Bootstrapper.Tests;

public sealed class BootstrapperRunnerTests
{
    [Fact]
    public async Task Blocked_preparation_does_not_prompt_or_execute()
    {
        var composition = new FakeComposition();
        var guard = new CountingGuard();
        var runtime = new FakeRuntime(new BootstrapperPreparation(null, [new BootstrapperBlocker("blocked")], guard));
        var prompt = new FakePrompt("sensitive-token");
        var status = new FakeStatus();

        await new BootstrapperRunner(runtime, prompt, status).RunAsync(BootstrapperAction.ExecuteUpgrade);

        Assert.Equal(0, prompt.Calls);
        Assert.Equal(0, composition.ExecuteCalls);
        Assert.True(guard.Disposed);
        Assert.Contains("sensitive-token", prompt.Secret);
        Assert.DoesNotContain("sensitive-token", string.Join("\n", status.Messages));
    }

    [Fact]
    public async Task Execute_prompts_only_after_prepare_and_disposes_lifetime_composition()
    {
        var events = new List<string>();
        var composition = new FakeComposition(events);
        var runtime = new FakeRuntime(new BootstrapperPreparation(composition, []), events);
        var prompt = new FakePrompt("one-use-token", events);

        var outcome = await new BootstrapperRunner(runtime, prompt, new FakeStatus()).RunAsync(BootstrapperAction.ExecuteUpgrade);

        Assert.Equal(new[] { "prepare", "prompt", "execute", "dispose" }, events);
        Assert.Equal("[REDACTED]", composition.ReceivedToken?.ToString());
        Assert.Equal(UpgradeOutcome.Succeeded, outcome?.Outcome);
        Assert.Equal(0, BootstrapperRunner.ToProcessExitCode(outcome));
    }

    [Fact]
    public async Task Reboot_required_success_is_preserved_for_the_bundle_exit_code()
    {
        var composition = new FakeComposition(rebootRequired: true);
        var runtime = new FakeRuntime(new BootstrapperPreparation(composition, []));
        var prompt = new FakePrompt("one-use-token");

        var result = await new BootstrapperRunner(runtime, prompt, new FakeStatus())
            .RunAsync(BootstrapperAction.ExecuteUpgrade);

        Assert.Equal(UpgradeOutcome.Succeeded, result?.Outcome);
        Assert.True(result?.RebootRequired);
        Assert.Equal(3010, BootstrapperRunner.ToProcessExitCode(result));
    }

    [Fact]
    public async Task Recovery_does_not_request_token_and_disposes_composition()
    {
        var events = new List<string>();
        var composition = new FakeComposition(events);
        var runtime = new FakeRuntime(new BootstrapperPreparation(composition, []), events);
        var prompt = new FakePrompt("must-not-be-used", events);

        await new BootstrapperRunner(runtime, prompt, new FakeStatus()).RunAsync(BootstrapperAction.RecoverInterrupted);

        Assert.Equal(new[] { "prepare", "recover", "dispose" }, events);
        Assert.Equal(0, prompt.Calls);
    }

    [Fact]
    public async Task Existing_operation_is_blocked_before_token_but_recovery_uses_same_runtime()
    {
        var runtime = new JournalAwareRuntime();
        var prompt = new FakePrompt("must-not-be-used");
        var status = new FakeStatus();
        var runner = new BootstrapperRunner(runtime, prompt, status);

        await runner.RunAsync(BootstrapperAction.ExecuteUpgrade);
        await runner.RunAsync(BootstrapperAction.RecoverInterrupted);

        Assert.Equal(0, prompt.Calls);
        Assert.Equal([BootstrapperAction.ExecuteUpgrade, BootstrapperAction.RecoverInterrupted], runtime.Actions);
        Assert.Equal(2, runtime.Guards.Count);
        Assert.All(runtime.Guards, guard => Assert.True(guard.Disposed));
        Assert.Contains(status.Messages, message => message.Contains("Выберите восстановление", StringComparison.Ordinal));
    }

    [Fact]
    public void Prepared_rollover_recovery_finishes_transaction_but_never_runs_core_on_pristine_successor()
    {
        var previous = Guid.NewGuid();
        var next = Guid.NewGuid();
        var retryCandidate = Guid.NewGuid();
        var old = RolledBackJournal(previous);
        var prepared = new UserJournalRolloverRecord(1, previous, next, UserJournalRolloverState.Prepared);

        var recoveryDuringPrepared = ProductionWindowsUpgradeRuntimeFactory.ResolveExistingOperation(
            retryCandidate, old, prepared, BootstrapperAction.RecoverInterrupted);
        Assert.Equal(next, recoveryDuringPrepared.OperationId);
        Assert.False(recoveryDuringPrepared.HasJournal);
        Assert.True(recoveryDuringPrepared.NeedsMachineRollover);

        // Models machine X→Y succeeding and the durable user commit completing before Recover starts.
        var committed = prepared with { State = UserJournalRolloverState.Committed };
        var pristine = PristineJournal(next);
        var recover = ProductionWindowsUpgradeRuntimeFactory.ResolveExistingOperation(
            retryCandidate, pristine, committed, BootstrapperAction.RecoverInterrupted);
        Assert.Equal(next, recover.OperationId);
        Assert.False(recover.HasJournal); // Production returns RecoveryJournalMissing before composition/Core.
        Assert.False(recover.NeedsMachineRollover);

        var execute = ProductionWindowsUpgradeRuntimeFactory.ResolveExistingOperation(
            retryCandidate, pristine, committed, BootstrapperAction.ExecuteUpgrade);
        Assert.Equal(next, execute.OperationId);
        Assert.False(execute.HasJournal);
        Assert.False(execute.NeedsMachineRollover);
    }

    [Fact]
    public void Committed_pristine_successor_recovery_reports_missing_and_execute_reuses_successor()
    {
        var previous = Guid.NewGuid();
        var next = Guid.NewGuid();
        var record = new UserJournalRolloverRecord(1, previous, next, UserJournalRolloverState.Committed);
        var pristine = PristineJournal(next);

        var recovery = ProductionWindowsUpgradeRuntimeFactory.ResolveExistingOperation(
            Guid.NewGuid(), pristine, record, BootstrapperAction.RecoverInterrupted);
        Assert.Equal(next, recovery.OperationId);
        Assert.False(recovery.HasJournal);

        var execute = ProductionWindowsUpgradeRuntimeFactory.ResolveExistingOperation(
            Guid.NewGuid(), pristine, record, BootstrapperAction.ExecuteUpgrade);
        Assert.Equal(next, execute.OperationId);
        Assert.False(execute.HasJournal);
    }

    private static UpgradeJournalDocument RolledBackJournal(Guid runId) => new(
        UpgradeJournalDocument.CurrentSchemaVersion, runId, 3, UpgradeJournalState.RolledBack,
        UpgradePhase.Rollback, [], "operation_failed");

    private static UpgradeJournalDocument PristineJournal(Guid runId) => new(
        UpgradeJournalDocument.CurrentSchemaVersion, runId, 0, UpgradeJournalState.InProgress,
        UpgradePhase.Preflight, []);

    [Fact]
    public async Task Exception_text_is_not_shown_to_user()
    {
        var runtime = new FakeRuntime(new BootstrapperPreparation(new ThrowingComposition(), []));
        var status = new FakeStatus();
        await new BootstrapperRunner(runtime, new FakePrompt("secret"), status)
            .RunAsync(BootstrapperAction.RecoverInterrupted);
        Assert.DoesNotContain("secret", string.Join("\n", status.Messages));
        Assert.DoesNotContain("token leak", string.Join("\n", status.Messages));
    }

    private sealed class FakeRuntime(BootstrapperPreparation result, List<string>? events = null)
        : IWindowsUpgradeBootstrapperRuntimeFactory
    {
        public ValueTask<BootstrapperPreparation> PrepareAsync(BootstrapperAction action, CancellationToken cancellationToken)
        {
            events?.Add("prepare");
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FakePrompt(string? value, List<string>? events = null) : IPlatformTokenPrompt
    {
        private readonly string? _value = value;
        public int Calls { get; private set; }
        public string? Secret => _value;
        public ValueTask<string?> RequestTokenAsync(CancellationToken cancellationToken)
        {
            Calls++;
            events?.Add("prompt");
            return ValueTask.FromResult(_value);
        }
    }

    private sealed class JournalAwareRuntime : IWindowsUpgradeBootstrapperRuntimeFactory
    {
        public List<BootstrapperAction> Actions { get; } = [];
        public List<CountingGuard> Guards { get; } = [];

        public ValueTask<BootstrapperPreparation> PrepareAsync(BootstrapperAction action, CancellationToken cancellationToken)
        {
            Actions.Add(action);
            var guard = new CountingGuard();
            Guards.Add(guard);
            return ValueTask.FromResult(action == BootstrapperAction.ExecuteUpgrade
                ? new BootstrapperPreparation(null, [new BootstrapperBlocker("ExistingOperationRequiresRecovery")], guard)
                : new BootstrapperPreparation(new FakeComposition(), [], guard));
        }
    }

    private sealed class CountingGuard : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    private sealed class FakeStatus : IBootstrapperStatusSink
    {
        public List<string> Messages { get; } = [];
        public void Show(string message) => Messages.Add(message);
    }

    private sealed class FakeComposition(List<string>? events = null, bool rebootRequired = false) : IOriginalUserUpgradeComposition
    {
        public int ExecuteCalls { get; private set; }
        public OneTimePlatformToken? ReceivedToken { get; private set; }
        public ValueTask<UpgradeExecutionResult> ExecuteAsync(OneTimePlatformToken token, CancellationToken cancellationToken)
        {
            ExecuteCalls++;
            ReceivedToken = token;
            events?.Add("execute");
            return ValueTask.FromResult(new UpgradeExecutionResult(UpgradeOutcome.Succeeded, Guid.NewGuid(), RebootRequired: rebootRequired));
        }
        public ValueTask<UpgradeExecutionResult> RecoverInterruptedAsync(CancellationToken cancellationToken)
        {
            events?.Add("recover");
            return ValueTask.FromResult(new UpgradeExecutionResult(UpgradeOutcome.Interrupted, Guid.NewGuid()));
        }
        public void Dispose() => events?.Add("dispose");
    }

    private sealed class ThrowingComposition : IOriginalUserUpgradeComposition
    {
        public ValueTask<UpgradeExecutionResult> ExecuteAsync(OneTimePlatformToken token, CancellationToken cancellationToken) =>
            ValueTask.FromException<UpgradeExecutionResult>(new InvalidOperationException("token leak"));
        public ValueTask<UpgradeExecutionResult> RecoverInterruptedAsync(CancellationToken cancellationToken) =>
            ValueTask.FromException<UpgradeExecutionResult>(new InvalidOperationException("token leak"));
        public void Dispose() { }
    }
}
