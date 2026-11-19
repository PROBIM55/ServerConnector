using Connector.Desktop.Services;
using Xunit;

namespace Connector.Desktop.Tests;

public sealed class SingleInstanceCoordinatorTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "connector-single-instance-" + Guid.NewGuid().ToString("N"));
    private readonly string _scope = Guid.NewGuid().ToString("N");

    [Fact]
    public async Task Later_launch_signals_primary_without_becoming_owner()
    {
        using var primary = Acquire();
        Assert.True(primary.IsPrimary);
        var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        primary.SetActivationHandler(() => activated.TrySetResult());

        using var later = Acquire();

        Assert.False(later.IsPrimary);
        Assert.True(later.ExistingInstanceSignaled);
        await activated.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Activation_during_startup_is_delivered_after_window_handler_registers()
    {
        using var primary = Acquire();
        using var later = Acquire();
        Assert.True(later.ExistingInstanceSignaled);
        var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        primary.SetActivationHandler(() => activated.TrySetResult());

        await activated.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Ownership_is_released_when_primary_exits()
    {
        var primary = Acquire();
        Assert.True(primary.IsPrimary);
        primary.Dispose();

        using var replacement = Acquire();

        Assert.True(replacement.IsPrimary);
        Assert.False(replacement.ExistingInstanceSignaled);
    }

    [Fact]
    public void Activation_handler_can_only_be_registered_by_primary_once()
    {
        using var primary = Acquire();
        primary.SetActivationHandler(() => { });
        Assert.Throws<InvalidOperationException>(() => primary.SetActivationHandler(() => { }));

        using var later = Acquire();
        Assert.Throws<InvalidOperationException>(() => later.SetActivationHandler(() => { }));
    }

    [Fact]
    public void Handler_failure_does_not_disable_primary_listener()
    {
        using var primary = Acquire();
        primary.SetActivationHandler(() => throw new InvalidOperationException("fixture"));

        using var first = Acquire();
        using var second = Acquire();

        Assert.True(first.ExistingInstanceSignaled);
        Assert.True(second.ExistingInstanceSignaled);
        Assert.True(primary.IsPrimary);
    }

    private SingleInstanceCoordinator Acquire() => SingleInstanceCoordinator.AcquireOrSignal(
        _directory,
        _scope,
        TimeSpan.FromSeconds(3));

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
