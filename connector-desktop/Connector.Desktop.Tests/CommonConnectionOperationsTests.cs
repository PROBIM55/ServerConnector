using Connector.Desktop.Services;
using Xunit;

namespace Connector.Desktop.Tests;

public sealed class CommonConnectionOperationsTests
{
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task RestoreWhileStartupIsPending_JoinsInsteadOfQueueingSecondConnection()
    {
        await using var operations = new CommonConnectionOperations();
        var finish = Signal();
        var calls = 0;
        var startup = operations.Start(CommonConnectionOperation.Restore, async (_, ct) =>
        {
            Interlocked.Increment(ref calls);
            await finish.Task.WaitAsync(ct);
        }, default);
        var restore = operations.Start(CommonConnectionOperation.Restore, (_, _) =>
            throw new InvalidOperationException("A duplicate connect must not execute."), default);

        Assert.Same(startup, restore);
        Assert.Equal(CommonConnectionOperation.Restore, operations.Progress.Operation);
        finish.SetResult();
        await restore.Completion.WaitAsync(Deadline);
        Assert.Equal(1, calls);
        Assert.Null(operations.Progress.Operation);
    }

    [Fact]
    public async Task SignOutCancelsStartupWait_AndWaitsForItsCleanupBeforeRevocation()
    {
        await using var operations = new CommonConnectionOperations();
        var cancelled = Signal();
        var cleanup = Signal();
        var revokeStarted = false;
        var startup = operations.Start(CommonConnectionOperation.Restore, async (_, ct) =>
        {
            try { await Task.Delay(Timeout.Infinite, ct); }
            finally { cancelled.SetResult(); await cleanup.Task; }
        }, default);

        var signout = operations.Start(CommonConnectionOperation.SignOut, (_, _) =>
        {
            revokeStarted = true;
            return Task.CompletedTask;
        }, default);
        await cancelled.Task.WaitAsync(Deadline);
        Assert.True(startup.Token.IsCancellationRequested);
        Assert.False(revokeStarted);
        Assert.Equal(CommonConnectionOperation.SignOut, operations.Progress.Operation);
        startup.Report("Stale failure from cancelled restore");
        Assert.Equal("Выполняем выход…", operations.Progress.Message);
        cleanup.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => startup.Completion.WaitAsync(Deadline));
        await signout.Completion.WaitAsync(Deadline);
        Assert.True(revokeStarted);
        Assert.False(operations.IsLatest(startup.Generation));
    }

    [Fact]
    public async Task BrowserApprovalContinuesIntoConnection_WithoutManualRestoreOrSecondBrowser()
    {
        await using var operations = new CommonConnectionOperations();
        var approval = Signal();
        var connected = false;
        var signIn = operations.Start(CommonConnectionOperation.SignIn, async (owner, ct) =>
        {
            await approval.Task.WaitAsync(ct);
            owner.Report("Вход подтверждён. Настраиваем защищённое соединение…");
            connected = true;
        }, default);
        var repeat = operations.Start(CommonConnectionOperation.SignIn, (_, _) =>
            throw new InvalidOperationException("A second browser must not open."), default);
        var restore = operations.Start(CommonConnectionOperation.Restore, (_, _) =>
            throw new InvalidOperationException("Restore cannot interrupt an approved sign-in."), default);
        Assert.Same(signIn, repeat);
        Assert.Same(signIn, restore);
        approval.SetResult();
        await signIn.Completion.WaitAsync(Deadline);
        Assert.True(connected);
        Assert.Null(operations.Progress.Operation);
    }

    [Fact]
    public async Task ExplicitSignInSupersedesStartup_AndOldCompletionCannotClearNewProgress()
    {
        await using var operations = new CommonConnectionOperations();
        var approval = Signal();
        var browserOpened = Signal();
        var startup = operations.Start(CommonConnectionOperation.Restore,
            async (_, ct) => await Task.Delay(Timeout.Infinite, ct), default);
        var login = operations.Start(CommonConnectionOperation.SignIn, async (owner, ct) =>
        {
            owner.Report("Ожидаем подтверждения в браузере");
            browserOpened.SetResult();
            await approval.Task.WaitAsync(ct);
        }, default);
        await browserOpened.Task.WaitAsync(Deadline);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => startup.Completion.WaitAsync(Deadline));
        Assert.Equal(login.Generation, operations.Progress.Generation);
        Assert.Equal(CommonConnectionOperation.SignIn, operations.Progress.Operation);
        approval.SetResult();
        await login.Completion.WaitAsync(Deadline);
    }

    [Fact]
    public async Task RepeatedSignOutAndStartupDuringSignOut_DoNotQueueNewWork()
    {
        await using var operations = new CommonConnectionOperations();
        var finish = Signal();
        var logout = operations.Start(CommonConnectionOperation.SignOut,
            async (_, ct) => await finish.Task.WaitAsync(ct), default);
        var duplicate = operations.Start(CommonConnectionOperation.SignOut, (_, _) =>
            throw new InvalidOperationException("Duplicate revoke"), default);
        var startup = operations.Start(CommonConnectionOperation.Restore, (_, _) =>
            throw new InvalidOperationException("Startup cannot reconnect a closing account"), default);
        Assert.Same(logout, duplicate);
        Assert.Same(logout, startup);
        finish.SetResult();
        await logout.Completion.WaitAsync(Deadline);
    }

    [Fact]
    public async Task FailedSignOutIsObserved_AndAllowsExplicitRetry()
    {
        await using var operations = new CommonConnectionOperations();
        var first = operations.Start(CommonConnectionOperation.SignOut,
            (_, _) => Task.FromException(new IOException("Revoke unavailable")), default);
        await Assert.ThrowsAsync<IOException>(() => first.Completion.WaitAsync(Deadline));
        Assert.Null(operations.Progress.Operation);
        var retry = operations.Start(CommonConnectionOperation.SignOut, (_, _) => Task.CompletedTask, default);
        await retry.Completion.WaitAsync(Deadline);
        Assert.True(retry.Generation > first.Generation);
    }

    [Fact]
    public async Task ApplicationExitCancelsBrowserOrConnectionWait()
    {
        var operations = new CommonConnectionOperations();
        var active = operations.Start(CommonConnectionOperation.SignIn,
            async (_, ct) => await Task.Delay(Timeout.Infinite, ct), default);
        await operations.DisposeAsync().AsTask().WaitAsync(Deadline);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active.Completion.WaitAsync(Deadline));
        Assert.Throws<ObjectDisposedException>(() => operations.Start(CommonConnectionOperation.Restore,
            (_, _) => Task.CompletedTask, default));
    }
}
