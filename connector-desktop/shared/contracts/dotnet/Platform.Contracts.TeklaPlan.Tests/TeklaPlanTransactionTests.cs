using Platform.Contracts.TeklaPlan;
using Xunit;

namespace Platform.Contracts.TeklaPlan.Tests;

public sealed class TeklaPlanTransactionTests
{
    [Fact]
    public void Applies_reads_back_and_commits_once_in_plan_order()
    {
        var events = new List<string>();
        var mutations = new ITeklaPreparedMutation<string>[]
        {
            new FakeMutation("one", events),
            new FakeMutation("two", events),
        };

        var result = TeklaPlanTransaction.Execute(mutations, () => events.Add("commit"));

        Assert.Equal(new[] { "one", "two" }, result);
        Assert.Equal(
            new[] { "apply:one", "apply:two", "readback:one", "readback:two", "commit" },
            events);
    }

    [Fact]
    public void Apply_failure_restores_only_applied_mutations_in_reverse_order()
    {
        var events = new List<string>();
        var failure = new InvalidOperationException("apply failed");
        var mutations = new ITeklaPreparedMutation<string>[]
        {
            new FakeMutation("one", events),
            new FakeMutation("two", events, applyError: failure),
            new FakeMutation("three", events),
        };

        var error = Assert.Throws<InvalidOperationException>(() =>
            TeklaPlanTransaction.Execute(mutations, () => events.Add("commit")));

        Assert.Same(failure, error);
        Assert.Equal(new[] { "apply:one", "apply:two", "restore:one", "commit" }, events);
    }

    [Fact]
    public void First_apply_failure_commits_self_restored_native_state()
    {
        var events = new List<string>();
        var failure = new InvalidOperationException("partial apply failed");
        var mutations = new ITeklaPreparedMutation<string>[]
        {
            new FakeMutation("one", events, applyError: failure),
        };

        var error = Assert.Throws<InvalidOperationException>(() =>
            TeklaPlanTransaction.Execute(mutations, () => events.Add("commit")));

        Assert.Same(failure, error);
        Assert.Equal(new[] { "apply:one", "commit" }, events);
    }

    [Fact]
    public void Readback_failure_rolls_back_all_applied_mutations()
    {
        var events = new List<string>();
        var failure = new InvalidOperationException("readback failed");
        var mutations = new ITeklaPreparedMutation<string>[]
        {
            new FakeMutation("one", events),
            new FakeMutation("two", events, readbackError: failure),
        };

        var error = Assert.Throws<InvalidOperationException>(() =>
            TeklaPlanTransaction.Execute(mutations, () => events.Add("commit")));

        Assert.Same(failure, error);
        Assert.Equal(
            new[]
            {
                "apply:one", "apply:two", "readback:one", "readback:two",
                "restore:two", "restore:one", "commit",
            },
            events);
    }

    [Fact]
    public void Commit_failure_restores_and_commits_the_restored_state()
    {
        var events = new List<string>();
        var commitCount = 0;
        var failure = new InvalidOperationException("commit failed");
        var mutation = new FakeMutation("one", events);

        var error = Assert.Throws<InvalidOperationException>(() =>
            TeklaPlanTransaction.Execute(
                new ITeklaPreparedMutation<string>[] { mutation },
                () =>
                {
                    events.Add("commit");
                    if (++commitCount == 1) throw failure;
                }));

        Assert.Same(failure, error);
        Assert.Equal(
            new[] { "apply:one", "readback:one", "commit", "restore:one", "commit" },
            events);
    }

    [Fact]
    public void Rollback_failures_are_reported_without_hiding_the_transaction_error()
    {
        var events = new List<string>();
        var transactionFailure = new InvalidOperationException("apply failed");
        var rollbackFailure = new InvalidOperationException("restore failed");
        var mutations = new ITeklaPreparedMutation<string>[]
        {
            new FakeMutation("one", events, restoreError: rollbackFailure),
            new FakeMutation("two", events, applyError: transactionFailure),
        };

        var error = Assert.Throws<TeklaPlanRollbackException>(() =>
            TeklaPlanTransaction.Execute(mutations, () => events.Add("commit")));

        Assert.Same(transactionFailure, error.TransactionError);
        Assert.Single(error.RollbackErrors);
        Assert.Same(rollbackFailure, error.RollbackErrors[0].InnerException);
    }

    private sealed class FakeMutation : ITeklaPreparedMutation<string>
    {
        private readonly List<string> _events;
        private readonly Exception? _applyError;
        private readonly Exception? _readbackError;
        private readonly Exception? _restoreError;

        public FakeMutation(
            string commandId,
            List<string> events,
            Exception? applyError = null,
            Exception? readbackError = null,
            Exception? restoreError = null)
        {
            CommandId = commandId;
            _events = events;
            _applyError = applyError;
            _readbackError = readbackError;
            _restoreError = restoreError;
        }

        public string CommandId { get; }

        public void Apply()
        {
            _events.Add($"apply:{CommandId}");
            if (_applyError is not null) throw _applyError;
        }

        public string Readback()
        {
            _events.Add($"readback:{CommandId}");
            if (_readbackError is not null) throw _readbackError;
            return CommandId;
        }

        public void Restore()
        {
            _events.Add($"restore:{CommandId}");
            if (_restoreError is not null) throw _restoreError;
        }
    }
}
