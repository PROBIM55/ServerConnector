// Single-thread queue для всех Tekla API calls. HTTP listeners принимают
// запросы и enqueue работу сюда; worker последовательно вызывает Tekla
// и возвращает результаты через TaskCompletionSource. См. plan §6.

#nullable enable

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Platform.Bridge.Desktop.Tekla.Logging;

namespace Platform.Bridge.Desktop.Tekla.Tekla
{
    public sealed class TeklaWorker : IDisposable
    {
        private readonly BlockingCollection<IJob> _queue = new();
        private readonly Thread _thread;
        private readonly TeklaConnection _connection;
        private readonly JsonLineLogger _log;
        private readonly CancellationTokenSource _cts = new();
        private volatile bool _lastKnownConnected;

        public TeklaWorker(TeklaConnection connection, JsonLineLogger log)
        {
            _connection = connection;
            _log = log;
            _thread = new Thread(Run)
            {
                Name = "TeklaWorker",
                IsBackground = false,
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        public bool LastKnownConnected => _lastKnownConnected;

        /// <summary>
        /// Enqueue работу с Tekla API. Func получает уже подключённый
        /// <see cref="Tekla.Structures.Model.Model"/>; делает вызовы синхронно,
        /// возвращает TResult который попадёт обратно к caller'у.
        /// При disconnect — задача fail'ится с <see cref="TeklaDisconnectedException"/>.
        /// </summary>
        public Task<TResult> RunAsync<TResult>(Func<global::Tekla.Structures.Model.Model, TResult> work,
                                               CancellationToken ct = default)
        {
            if (ct.IsCancellationRequested) return Task.FromCanceled<TResult>(ct);

            var tcs = new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var job = new Job<TResult>(work, tcs, ct);
            _queue.Add(job);
            return tcs.Task;
        }

        private void Run()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    IJob job;
                    try { job = _queue.Take(_cts.Token); }
                    catch (OperationCanceledException) { break; }

                    if (job.IsCancellationRequested)
                    {
                        job.Cancel();
                        continue;
                    }
                    RunOne(job);
                }
            }
            finally
            {
                _log.Info("tekla.worker.stopped");
            }
        }

        /// <summary>
        /// Выполнить job с self-recovery от stale Tekla remote-references.
        /// Логика: TryRun → если упало с stale-handle (после рестарта Tekla),
        /// дропаем connection и ретраим один раз. Если повторно stale → выбрасываем
        /// <see cref="TeklaStaleHandleException"/>, HTTP-уровень вернёт 503/BRIDGE_STALE_TEKLA,
        /// Connector сделает hard-restart Bridge.Desktop как fallback.
        /// </summary>
        private void RunOne(IJob job)
        {
            if (job.IsCancellationRequested)
            {
                job.Cancel();
                return;
            }

            // Attempt 1.
            global::Tekla.Structures.Model.Model? model;
            try
            {
                model = _connection.EnsureConnected();
                _lastKnownConnected = true;
            }
            catch (TeklaDisconnectedException ex)
            {
                _lastKnownConnected = false;
                job.Fail(ex);
                _log.Warn("tekla.worker.disconnected", new { err = ex.Message });
                return;
            }
            catch (Exception ex)
            {
                _lastKnownConnected = false;
                job.Fail(ex);
                _log.Error("tekla.worker.connect-failed", new { err = ex.Message, type = ex.GetType().Name });
                return;
            }

            var firstError = job.TryRun(model);
            if (firstError is null)
            {
                job.Commit();
                return;
            }

            if (firstError is OperationCanceledException || job.IsCancellationRequested)
            {
                job.Cancel();
                _log.Info("tekla.worker.cancelled");
                return;
            }

            if (!StaleTeklaDetection.IsStaleHandle(firstError))
            {
                job.Fail(firstError);
                _log.Error("tekla.worker.unhandled", new { err = firstError.Message, type = firstError.GetType().Name });
                return;
            }

            // Stale-handle detected — invalidate connection и retry один раз.
            _log.Warn("tekla.worker.stale.detected", new { err = firstError.Message, type = firstError.GetType().Name });
            _connection.Invalidate();

            global::Tekla.Structures.Model.Model? retryModel;
            try
            {
                retryModel = _connection.EnsureConnected();
                _lastKnownConnected = true;
            }
            catch (Exception ex)
            {
                _lastKnownConnected = false;
                _log.Error("tekla.worker.stale.reconnect-failed", new { err = ex.Message, type = ex.GetType().Name });
                job.Fail(new TeklaStaleHandleException(firstError));
                return;
            }

            _log.Info("tekla.worker.stale.reconnected");
            var secondError = job.TryRun(retryModel);
            if (secondError is null)
            {
                job.Commit();
                _log.Info("tekla.worker.stale.recovered");
                return;
            }

            if (secondError is OperationCanceledException || job.IsCancellationRequested)
            {
                job.Cancel();
                _log.Info("tekla.worker.cancelled");
                return;
            }

            // Повторно stale (или другая ошибка) — выходим с TeklaStaleHandleException
            // если опять признаки stale; иначе пробрасываем оригинальную ошибку.
            if (StaleTeklaDetection.IsStaleHandle(secondError))
            {
                _log.Error("tekla.worker.stale.retry-failed", new { err = secondError.Message, type = secondError.GetType().Name });
                job.Fail(new TeklaStaleHandleException(secondError));
            }
            else
            {
                _log.Error("tekla.worker.retry-failed", new { err = secondError.Message, type = secondError.GetType().Name });
                job.Fail(secondError);
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _queue.CompleteAdding();
            try { _thread.Join(TimeSpan.FromSeconds(5)); } catch { /* ignore */ }
            _queue.Dispose();
            _cts.Dispose();
        }

        private interface IJob
        {
            bool IsCancellationRequested { get; }
            /// <summary>Выполняет work; возвращает null при успехе, exception при ошибке (TCS НЕ обновляется).</summary>
            Exception? TryRun(global::Tekla.Structures.Model.Model model);
            /// <summary>Завершить TCS успехом после удачного TryRun.</summary>
            void Commit();
            /// <summary>Завершить TCS ошибкой.</summary>
            void Fail(Exception exception);
            /// <summary>Завершить TCS отменой и освободить регистрацию token.</summary>
            void Cancel();
        }

        private sealed class Job<TResult> : IJob
        {
            private readonly Func<global::Tekla.Structures.Model.Model, TResult> _work;
            private readonly TaskCompletionSource<TResult> _tcs;
            private readonly CancellationToken _ct;
            private readonly CancellationTokenRegistration _registration;
            private TResult? _lastResult;
            private bool _hasResult;

            public Job(
                Func<global::Tekla.Structures.Model.Model, TResult> work,
                TaskCompletionSource<TResult> tcs,
                CancellationToken ct)
            {
                _work = work;
                _tcs = tcs;
                _ct = ct;
                _registration = ct.CanBeCanceled
                    ? ct.Register(() => _tcs.TrySetCanceled(ct))
                    : default;
            }

            public bool IsCancellationRequested => _ct.IsCancellationRequested;

            public Exception? TryRun(global::Tekla.Structures.Model.Model model)
            {
                _hasResult = false;
                try
                {
                    _ct.ThrowIfCancellationRequested();
                    _lastResult = _work(model);
                    _ct.ThrowIfCancellationRequested();
                    _hasResult = true;
                    return null;
                }
                catch (Exception ex)
                {
                    return ex;
                }
            }

            public void Commit()
            {
                if (!_hasResult) throw new InvalidOperationException("Commit called without successful TryRun.");
                if (_ct.IsCancellationRequested)
                {
                    Cancel();
                    return;
                }
                _tcs.TrySetResult(_lastResult!);
                _registration.Dispose();
            }

            public void Fail(Exception exception)
            {
                _tcs.TrySetException(exception);
                _registration.Dispose();
            }

            public void Cancel()
            {
                if (_ct.CanBeCanceled) _tcs.TrySetCanceled(_ct);
                else _tcs.TrySetCanceled();
                _registration.Dispose();
            }
        }
    }
}
