// POST /pick/axis — interactive: блокирующий вызов Picker.PickPoints с
// PICK_TWO_POINTS. Tekla фокусирует пользователя на picker'е, мы ждём пока
// он клацнет дважды или нажмёт Esc. Возвращает start/end (mm).
//
// PickerInterruptedException не определён как отдельный тип в SDK (Tekla 2025
// проверено Reflection'ом). На Esc поднимается ApplicationException — ловим
// и переводим в PICK_CANCELLED.
//
// Внутренний timeout по умолчанию 180 сек (plan §9.2.1). Connector добавляет
// 30 сек запас (210), Server 240, Web 270 — каждый слой умирает раньше
// верхнего.

#nullable enable

using System;
using System.Collections;
using System.Threading;
using System.Threading.Tasks;
using Platform.Bridge.Desktop.Tekla.Tekla;
using Tekla.Structures.Geometry3d;
using Tekla.Structures.Model.UI;

namespace Platform.Bridge.Desktop.Tekla.Http
{
    public sealed class PickHandler
    {
        private const int InternalPickTimeoutSeconds = 180;
        private readonly TeklaWorker _worker;

        public PickHandler(TeklaWorker worker) { _worker = worker; }

        public async Task<HttpResult> PickAxisAsync(RequestContext request, CancellationToken ct)
        {
            using var pickCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            pickCts.CancelAfter(TimeSpan.FromSeconds(InternalPickTimeoutSeconds));

            try
            {
                var (start, end) = await _worker.RunAsync(_ =>
                {
                    var picker = new Picker();
                    ArrayList points;
                    try
                    {
                        points = picker.PickPoints(Picker.PickPointEnum.PICK_TWO_POINTS, "Pick axis: start, end");
                    }
                    catch (ApplicationException)
                    {
                        // Tekla SDK сигналит Esc через generic ApplicationException
                        // (см. Reflection: PickerInterruptedException не определён в 2025 SDK).
                        throw new PickCancelledException();
                    }

                    if (points is null || points.Count < 2)
                        throw new PickCancelledException();

                    return ((Point)points[0]!, (Point)points[1]!);
                }, pickCts.Token);

                return HttpResult.Ok(new
                {
                    ok = true,
                    start = new { x = start.X, y = start.Y, z = start.Z },
                    end = new { x = end.X, y = end.Y, z = end.Z },
                });
            }
            catch (PickCancelledException)
            {
                return new HttpResult(409, new
                {
                    ok = false,
                    errorCode = "PICK_CANCELLED",
                    message = "User cancelled axis pick (Esc).",
                });
            }
            catch (OperationCanceledException) when (pickCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                return new HttpResult(408, new
                {
                    ok = false,
                    errorCode = "PICK_TIMEOUT",
                    message = $"Axis pick exceeded {InternalPickTimeoutSeconds}s.",
                });
            }
            catch (TeklaDisconnectedException ex)
            {
                return HttpResult.ServerError("TEKLA_DISCONNECTED", ex.Message);
            }
            catch (TeklaStaleHandleException)
            {
                return HttpResult.ServiceUnavailable("BRIDGE_STALE_TEKLA",
                    "Tekla remote references became stale after Tekla restart; in-process reconnect failed. Connector should hard-restart Bridge.Desktop.");
            }
            catch (Exception ex)
            {
                return HttpResult.ServerError("INTERNAL_ERROR", $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        private sealed class PickCancelledException : Exception { }
    }
}
