using System.Collections.Concurrent;
using System.Runtime.Versioning;

namespace Platform.Cad.AutoCad.Adapter.Sessions;

/// <summary>
/// Однопоточный исполнитель на выделенном STA-потоке — паттерн TeklaWorker
/// из Bridge.Desktop. Все COM-вызовы к AutoCAD (ROT enumeration, late-bound
/// dynamic) идут строго через него: dynamic-вызовы COM из MTA/threadpool
/// нестабильны (apartment affinity RCW). Поток ленивый: создаётся при первом
/// RunAsync, фоновый (не держит процесс при выходе).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class StaExecutor : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly object _startGate = new();
    private Thread? _thread;
    private bool _disposed;

    public Task<TResult> RunAsync<TResult>(Func<TResult> work, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureThreadStarted();

        var tcs = new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));

        _queue.Add(() =>
        {
            if (tcs.Task.IsCompleted)
            {
                return; // отменено, пока ждали очередь
            }

            try
            {
                tcs.TrySetResult(work());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        return tcs.Task;
    }

    private void EnsureThreadStarted()
    {
        if (_thread is not null)
        {
            return;
        }

        lock (_startGate)
        {
            if (_thread is not null)
            {
                return;
            }

            var thread = new Thread(Run)
            {
                Name = "AutoCadComWorker",
                IsBackground = true
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            _thread = thread;
        }
    }

    private void Run()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                Action workItem;
                try
                {
                    workItem = _queue.Take(_cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (InvalidOperationException)
                {
                    break; // CompleteAdding
                }

                workItem();
            }
        }
        catch
        {
            // Поток-исполнитель не должен ронять процесс.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();
        _queue.CompleteAdding();
        try
        {
            _thread?.Join(TimeSpan.FromSeconds(3));
        }
        catch
        {
            // best effort
        }

        _queue.Dispose();
        _cts.Dispose();
    }
}
