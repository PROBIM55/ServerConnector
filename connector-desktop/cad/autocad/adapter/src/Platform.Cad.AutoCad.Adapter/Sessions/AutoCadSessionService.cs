using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Platform.Cad.AutoCad.Adapter.Sessions;

/// <summary>
/// Production-исполнитель session-операций над запущенным AutoCAD:
///   ROT-walk (<see cref="AutoCadRunningObjectTable"/>) → late-bound dynamic COM →
///   операции (ping/drawEntities/eraseByHandles/zoomExtents).
///
/// Дизайн:
///  - все COM-вызовы на одном выделенном STA-потоке (<see cref="StaExecutor"/>);
///  - на каждый запрос — свежий ROT-walk и release всех RCW в конце (никаких
///    кэшированных Application между job'ами — protect от stale-handle после
///    перезапуска AutoCAD);
///  - busy (RPC_E_CALL_REJECTED / RPC_E_SERVERCALL_RETRYLATER) — ретраи
///    с backoff через <see cref="ComRetryPolicy"/>, после исчерпания — AUTOCAD_SESSION_BUSY;
///  - сборка без AutoCAD: COM полностью late-bound (никаких interop-сборок),
///    на машине без AutoCAD перечисление возвращает пустой список.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AutoCadSessionService : IAutoCadSessionExecutor, IDisposable
{
    private static readonly Lazy<AutoCadSessionService> SharedInstance = new(() => new AutoCadSessionService());

    /// <summary>Общий инстанс процесса (UI-контроллер и адаптер делят один STA-поток).</summary>
    public static AutoCadSessionService Shared => SharedInstance.Value;

    private readonly StaExecutor _sta = new();

    public Task<IReadOnlyList<AutoCadSessionInfo>> ListSessionsAsync(CancellationToken cancellationToken)
        => _sta.RunAsync<IReadOnlyList<AutoCadSessionInfo>>(
            () =>
            {
                var entries = AutoCadRunningObjectTable.GetAutoCadApplications();
                try
                {
                    return BuildSessionInfos(entries).Select(pair => pair.Info).ToList();
                }
                finally
                {
                    ReleaseEntries(entries);
                }
            },
            cancellationToken);

    /// <summary>
    /// Подключение к конкретной сессии: держит COM Application до Dispose.
    /// Для одноразовых job'ов используйте <see cref="ExecuteAsync"/> (connect-per-job).
    /// </summary>
    public Task<AutoCadSessionHandle> ConnectAsync(string sessionKey, CancellationToken cancellationToken)
        => _sta.RunAsync(
            () =>
            {
                var entries = AutoCadRunningObjectTable.GetAutoCadApplications();
                var pairs = BuildSessionInfos(entries);
                var resolution = AutoCadSessionResolver.Resolve(sessionKey, selectedKey: null, pairs.Select(p => p.Info).ToList());
                if (!resolution.IsResolved)
                {
                    ReleaseEntries(entries);
                    throw new InvalidOperationException($"{resolution.ErrorCode}: {resolution.Message}");
                }

                var target = pairs.First(p => ReferenceEquals(p.Info, resolution.Session));
                // Сравниваем по COM-объекту, не по record'у: один инстанс может быть
                // в ROT под несколькими моникерами с одним и тем же RCW.
                foreach (var entry in entries.Where(e => !ReferenceEquals(e.Application, target.Entry.Application)))
                {
                    TryRelease(entry.Application);
                }

                return new AutoCadSessionHandle(_sta, target.Info, target.Entry.Application);
            },
            cancellationToken);

    public Task<AutoCadSessionOperationResult> ExecuteAsync(AutoCadSessionRequest request, CancellationToken cancellationToken)
        => _sta.RunAsync(() => ExecuteCore(request, cancellationToken), cancellationToken);

    // ─────────────────────────── core (STA thread) ───────────────────────────

    private static AutoCadSessionOperationResult ExecuteCore(AutoCadSessionRequest request, CancellationToken cancellationToken)
    {
        var entries = AutoCadRunningObjectTable.GetAutoCadApplications();
        try
        {
            var pairs = BuildSessionInfos(entries);
            var sessions = pairs.Select(p => p.Info).ToList();
            var resolution = AutoCadSessionResolver.Resolve(
                request.SessionKey,
                AutoCadSessionSelection.SelectedSessionKey,
                sessions);

            if (!resolution.IsResolved)
            {
                return new AutoCadSessionOperationResult(false, resolution.ErrorCode, resolution.Message);
            }

            var target = pairs.First(p => ReferenceEquals(p.Info, resolution.Session));
            try
            {
                return request.Action switch
                {
                    AutoCadSessionAction.Ping => PingCore(target.Info, target.Entry.Application, cancellationToken),
                    AutoCadSessionAction.DrawEntities => DrawEntitiesCore(target.Info, target.Entry.Application, request.Entities, cancellationToken),
                    AutoCadSessionAction.EraseByHandles => EraseByHandlesCore(target.Info, target.Entry.Application, request.Handles, cancellationToken),
                    AutoCadSessionAction.ZoomExtents => ZoomExtentsCore(target.Info, target.Entry.Application, cancellationToken),
                    _ => new AutoCadSessionOperationResult(false, AutoCadSessionErrorCodes.PayloadInvalid, $"Action {request.Action} is not implemented.")
                };
            }
            catch (Exception ex) when (ComRetryPolicy.IsBusyException(ex))
            {
                return new AutoCadSessionOperationResult(
                    false,
                    AutoCadSessionErrorCodes.SessionBusy,
                    $"AutoCAD session {target.Info.SessionKey} is busy (modal dialog or active command); retries exhausted. {ex.Message}",
                    target.Info);
            }
            catch (COMException ex)
            {
                return new AutoCadSessionOperationResult(
                    false,
                    AutoCadSessionErrorCodes.ComError,
                    $"AutoCAD COM call failed (HRESULT 0x{ex.HResult:X8}): {ex.Message}",
                    target.Info);
            }
        }
        finally
        {
            ReleaseEntries(entries);
        }
    }

    private static AutoCadSessionOperationResult PingCore(AutoCadSessionInfo info, object application, CancellationToken ct)
    {
        dynamic app = application;
        var version = Retry(() => (string)app.Version.ToString(), ct);
        var documentsCount = Retry(() => (int)app.Documents.Count, ct);

        string? documentName = null;
        string? documentPath = null;
        int? modelSpaceCount = null;
        if (documentsCount > 0)
        {
            try
            {
                dynamic document = Retry(() => app.ActiveDocument, ct);
                documentName = Retry(() => (string)document.Name, ct);
                try
                {
                    documentPath = Retry(() => (string)document.FullName, ct);
                }
                catch (COMException)
                {
                    // несохранённый документ — FullName может быть недоступен
                }

                try
                {
                    modelSpaceCount = Retry(() => (int)document.ModelSpace.Count, ct);
                }
                catch (COMException)
                {
                    // ModelSpace может быть недоступен в спец-режимах — не критично для ping
                }
            }
            catch (COMException)
            {
                // ActiveDocument недоступен — оставляем null-поля
            }
        }

        var ping = new AutoCadPingInfo(version, documentName, documentPath, documentsCount, modelSpaceCount);
        return new AutoCadSessionOperationResult(
            true,
            Message: $"AutoCAD {version}, документов: {documentsCount}" +
                     (documentName is null ? string.Empty : $", активный: {documentName}") +
                     (modelSpaceCount is null ? string.Empty : $" ({modelSpaceCount} объектов в модели)"),
            Session: info,
            Ping: ping);
    }

    private static AutoCadSessionOperationResult DrawEntitiesCore(
        AutoCadSessionInfo info,
        object application,
        IReadOnlyList<AutoCadEntitySpec> entities,
        CancellationToken ct)
    {
        if (!TryGetActiveDocument(application, ct, out var documentObject))
        {
            return NoActiveDocument(info, "drawEntities");
        }

        dynamic document = documentObject!;
        dynamic modelSpace = Retry(() => document.ModelSpace, ct);

        var handles = new List<string>(entities.Count);
        foreach (var spec in entities)
        {
            ct.ThrowIfCancellationRequested();
            dynamic created;
            switch (spec)
            {
                case AutoCadLineSpec line:
                    created = Retry(() => modelSpace.AddLine(
                        AutoCadEntityVariantConverter.LineStart(line),
                        AutoCadEntityVariantConverter.LineEnd(line)), ct);
                    break;

                case AutoCadPolylineSpec polyline:
                    created = Retry(() => modelSpace.AddPolyline(
                        AutoCadEntityVariantConverter.PolylineFlat3d(polyline)), ct);
                    if (polyline.Closed)
                    {
                        Retry<object?>(() =>
                        {
                            created.Closed = true;
                            return null;
                        }, ct);
                    }

                    break;

                case AutoCadCircleSpec circle:
                    created = Retry(() => modelSpace.AddCircle(
                        AutoCadEntityVariantConverter.CircleCenter(circle), circle.R), ct);
                    break;

                case AutoCadTextSpec text:
                    created = Retry(() => modelSpace.AddText(
                        text.Value,
                        AutoCadEntityVariantConverter.TextInsertionPoint(text),
                        text.Height), ct);
                    break;

                default:
                    return new AutoCadSessionOperationResult(
                        false,
                        AutoCadSessionErrorCodes.PayloadInvalid,
                        $"Entity spec {spec.GetType().Name} is not supported.",
                        info,
                        CreatedHandles: handles);
            }

            handles.Add(Retry(() => (string)created.Handle.ToString(), ct));
        }

        return new AutoCadSessionOperationResult(
            true,
            Message: $"Создано объектов: {handles.Count}.",
            Session: info,
            CreatedHandles: handles);
    }

    private static AutoCadSessionOperationResult EraseByHandlesCore(
        AutoCadSessionInfo info,
        object application,
        IReadOnlyList<string> handles,
        CancellationToken ct)
    {
        if (!TryGetActiveDocument(application, ct, out var documentObject))
        {
            return NoActiveDocument(info, "eraseByHandles");
        }

        dynamic document = documentObject!;
        var erased = 0;
        var notFound = new List<string>();
        foreach (var handle in handles)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                dynamic entity = Retry(() => document.HandleToObject(handle), ct);
                Retry<object?>(() =>
                {
                    entity.Erase();
                    return null;
                }, ct);
                erased++;
            }
            catch (Exception ex) when (ex is COMException or ArgumentException && !ComRetryPolicy.IsBusyException(ex))
            {
                // Handle не существует (или объект уже стёрт) — best effort, копим список.
                notFound.Add(handle);
            }
        }

        return new AutoCadSessionOperationResult(
            true,
            Message: $"Удалено объектов: {erased} из {handles.Count}.",
            Session: info,
            ErasedCount: erased,
            NotFoundHandles: notFound);
    }

    private static AutoCadSessionOperationResult ZoomExtentsCore(AutoCadSessionInfo info, object application, CancellationToken ct)
    {
        if (!TryGetActiveDocument(application, ct, out _))
        {
            return NoActiveDocument(info, "zoomExtents");
        }

        dynamic app = application;
        Retry<object?>(() =>
        {
            app.ZoomExtents();
            return null;
        }, ct);

        return new AutoCadSessionOperationResult(true, Message: "ZoomExtents выполнен.", Session: info);
    }

    private static AutoCadSessionOperationResult NoActiveDocument(AutoCadSessionInfo info, string action)
        => new(
            false,
            AutoCadSessionErrorCodes.NoActiveDocument,
            $"AutoCAD session {info.SessionKey} has no open document; action '{action}' requires an active drawing.",
            info);

    private static bool TryGetActiveDocument(object application, CancellationToken ct, out object? document)
    {
        document = null;
        dynamic app = application;
        try
        {
            var documentsCount = Retry(() => (int)app.Documents.Count, ct);
            if (documentsCount == 0)
            {
                return false;
            }

            document = Retry(() => app.ActiveDocument, ct);
            return document is not null;
        }
        catch (COMException ex) when (!ComRetryPolicy.IsBusyException(ex))
        {
            return false;
        }
    }

    // ─────────────────────────── session info from ROT ───────────────────────────

    private sealed record SessionPair(AutoCadSessionInfo Info, AutoCadRunningObjectTable.RotEntry Entry);

    private static List<SessionPair> BuildSessionInfos(List<AutoCadRunningObjectTable.RotEntry> entries)
    {
        var pairs = new List<SessionPair>(entries.Count);
        var seenPids = new HashSet<int>();
        foreach (var entry in entries)
        {
            var info = TryReadSessionInfo(entry);
            if (info is null || !seenPids.Add(info.ProcessId))
            {
                continue; // нечитабельный или дубликат (несколько моникеров одного инстанса)
            }

            pairs.Add(new SessionPair(info, entry));
        }

        return pairs;
    }

    private static AutoCadSessionInfo? TryReadSessionInfo(AutoCadRunningObjectTable.RotEntry entry)
    {
        dynamic app = entry.Application;

        var pid = AutoCadRunningObjectTable.TryParseProcessIdFromDisplayName(entry.DisplayName);
        if (pid <= 0)
        {
            try
            {
                long hwnd = Convert.ToInt64(Retry(() => app.HWND, CancellationToken.None));
                pid = AutoCadRunningObjectTable.GetProcessIdFromHwnd(hwnd);
            }
            catch
            {
                return null;
            }
        }

        if (pid <= 0)
        {
            return null;
        }

        string version;
        try
        {
            version = Retry(() => (string)app.Version.ToString(), CancellationToken.None);
        }
        catch
        {
            version = "?";
        }

        string caption;
        try
        {
            caption = Retry(() => (string)app.Caption.ToString(), CancellationToken.None);
        }
        catch
        {
            caption = string.Empty;
        }

        string? documentName = null;
        string? documentPath = null;
        try
        {
            var documentsCount = Retry(() => (int)app.Documents.Count, CancellationToken.None);
            if (documentsCount > 0)
            {
                dynamic document = Retry(() => app.ActiveDocument, CancellationToken.None);
                documentName = Retry(() => (string)document.Name, CancellationToken.None);
                try
                {
                    documentPath = Retry(() => (string)document.FullName, CancellationToken.None);
                }
                catch
                {
                    // несохранённый документ
                }
            }
        }
        catch
        {
            // нет документов / документ недоступен — сессия всё равно валидна
        }

        return new AutoCadSessionInfo(AutoCadSessionKey.Format(pid), pid, version, documentName, documentPath, caption);
    }

    private static T Retry<T>(Func<T> action, CancellationToken ct)
        => ComRetryPolicy.Execute(action, cancellationToken: ct);

    private static void ReleaseEntries(List<AutoCadRunningObjectTable.RotEntry> entries)
    {
        foreach (var entry in entries)
        {
            TryRelease(entry.Application);
        }
    }

    private static void TryRelease(object comObject)
    {
        try
        {
            if (Marshal.IsComObject(comObject))
            {
                Marshal.FinalReleaseComObject(comObject);
            }
        }
        catch
        {
            // best effort
        }
    }

    public void Dispose()
    {
        _sta.Dispose();
    }
}

/// <summary>
/// Долгоживущее подключение к одной сессии (держит COM Application).
/// Все операции — через тот же STA-поток сервиса. Dispose освобождает RCW.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AutoCadSessionHandle : IDisposable
{
    private readonly StaExecutor _sta;
    private object? _application;

    internal AutoCadSessionHandle(StaExecutor sta, AutoCadSessionInfo session, object application)
    {
        _sta = sta;
        Session = session;
        _application = application;
    }

    public AutoCadSessionInfo Session { get; }

    /// <summary>Живой ли COM-канал: лёгкий вызов Version на STA-потоке.</summary>
    public Task<bool> IsAliveAsync(CancellationToken cancellationToken = default)
    {
        var application = _application;
        if (application is null)
        {
            return Task.FromResult(false);
        }

        return _sta.RunAsync(
            () =>
            {
                try
                {
                    dynamic app = application;
                    _ = (string)app.Version.ToString();
                    return true;
                }
                catch
                {
                    return false;
                }
            },
            cancellationToken);
    }

    public void Dispose()
    {
        var application = Interlocked.Exchange(ref _application, null);
        if (application is null)
        {
            return;
        }

        try
        {
            // Release на том же STA-потоке, где объект жил.
            _sta.RunAsync<object?>(
                () =>
                {
                    try
                    {
                        if (Marshal.IsComObject(application))
                        {
                            Marshal.FinalReleaseComObject(application);
                        }
                    }
                    catch
                    {
                        // best effort
                    }

                    return null;
                }).Wait(TimeSpan.FromSeconds(3));
        }
        catch
        {
            // best effort
        }
    }
}
