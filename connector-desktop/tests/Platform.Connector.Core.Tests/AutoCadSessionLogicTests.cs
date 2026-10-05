// Wave B: unit-тесты pure-логики session-слоя AutoCAD (без COM/AutoCAD):
// ключи сессий, резолв цели, парсинг payload, конвертация сущностей в
// variant-массивы, retry-политика busy-HRESULT'ов, динамические capabilities.

using System.Runtime.InteropServices;
using System.Text.Json;
using Platform.Cad.AutoCad.Adapter.Sessions;
using Platform.Connector.Core;

namespace Platform.Connector.Core.Tests;

public sealed class AutoCadSessionLogicTests
{
    // ────────── AutoCadSessionKey ──────────

    [Fact]
    public void SessionKey_FormatAndParse_RoundTrip()
    {
        var key = AutoCadSessionKey.Format(34736);

        Assert.Equal("acad-34736", key);
        Assert.True(AutoCadSessionKey.TryParseProcessId(key, out var pid));
        Assert.Equal(34736, pid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("acad-")]
    [InlineData("acad-abc")]
    [InlineData("acad--5")]
    [InlineData("tekla-123")]
    [InlineData("34736")]
    public void SessionKey_TryParse_RejectsInvalid(string? raw)
    {
        Assert.False(AutoCadSessionKey.TryParseProcessId(raw, out _));
    }

    [Fact]
    public void SessionKey_TryParse_IsCaseInsensitiveAndTrims()
    {
        Assert.True(AutoCadSessionKey.TryParseProcessId("  ACAD-42  ", out var pid));
        Assert.Equal(42, pid);
    }

    // ────────── AutoCadSessionResolver ──────────

    private static AutoCadSessionInfo Session(int pid, string? doc = null)
        => new(AutoCadSessionKey.Format(pid), pid, "24.1", doc, doc is null ? null : $@"C:\dwg\{doc}", $"AutoCAD {pid}");

    [Fact]
    public void Resolver_NoSessions_ReturnsNoRunningSessions()
    {
        var resolution = AutoCadSessionResolver.Resolve(null, null, []);

        Assert.False(resolution.IsResolved);
        Assert.Equal(AutoCadSessionErrorCodes.NoRunningSessions, resolution.ErrorCode);
    }

    [Fact]
    public void Resolver_RequestedKey_Found()
    {
        var sessions = new[] { Session(1), Session(2) };

        var resolution = AutoCadSessionResolver.Resolve("acad-2", "acad-1", sessions);

        Assert.True(resolution.IsResolved);
        Assert.Equal(2, resolution.Session!.ProcessId);
    }

    [Fact]
    public void Resolver_RequestedKey_Missing_ReturnsNotFound()
    {
        var sessions = new[] { Session(1) };

        var resolution = AutoCadSessionResolver.Resolve("acad-99", null, sessions);

        Assert.False(resolution.IsResolved);
        Assert.Equal(AutoCadSessionErrorCodes.SessionNotFound, resolution.ErrorCode);
        Assert.Contains("acad-1", resolution.Message);
    }

    [Fact]
    public void Resolver_SelectedKey_Used_WhenAlive()
    {
        var sessions = new[] { Session(1), Session(2) };

        var resolution = AutoCadSessionResolver.Resolve(null, "ACAD-2", sessions);

        Assert.True(resolution.IsResolved);
        Assert.Equal(2, resolution.Session!.ProcessId);
    }

    [Fact]
    public void Resolver_StaleSelectedKey_FallsBackToSingleSession()
    {
        var sessions = new[] { Session(7) };

        var resolution = AutoCadSessionResolver.Resolve(null, "acad-99", sessions);

        Assert.True(resolution.IsResolved);
        Assert.Equal(7, resolution.Session!.ProcessId);
    }

    [Fact]
    public void Resolver_StaleSelectedKey_MultipleSessions_RequiresReselect()
    {
        var sessions = new[] { Session(1), Session(2) };

        var resolution = AutoCadSessionResolver.Resolve(null, "acad-99", sessions);

        Assert.False(resolution.IsResolved);
        Assert.Equal(AutoCadSessionErrorCodes.SessionNotSelected, resolution.ErrorCode);
    }

    [Fact]
    public void Resolver_NoKeys_SingleSession_AutoPick()
    {
        var resolution = AutoCadSessionResolver.Resolve(null, null, [Session(5)]);

        Assert.True(resolution.IsResolved);
        Assert.Equal(5, resolution.Session!.ProcessId);
    }

    [Fact]
    public void Resolver_NoKeys_MultipleSessions_ReturnsNotSelected()
    {
        var resolution = AutoCadSessionResolver.Resolve(null, null, [Session(1), Session(2)]);

        Assert.False(resolution.IsResolved);
        Assert.Equal(AutoCadSessionErrorCodes.SessionNotSelected, resolution.ErrorCode);
        Assert.Contains("acad-1", resolution.Message);
        Assert.Contains("acad-2", resolution.Message);
    }

    // ────────── AutoCadSessionPayloadParser ──────────

    private static JsonElement Json(object src)
        => JsonDocument.Parse(JsonSerializer.Serialize(src)).RootElement.Clone();

    [Fact]
    public void Parser_IsSessionMode_DetectsSessionPayloads()
    {
        Assert.True(AutoCadSessionPayloadParser.IsSessionMode(Json(new { mode = "session", action = "ping" })));
        Assert.True(AutoCadSessionPayloadParser.IsSessionMode(Json(new { mode = "SESSION" })));
        Assert.False(AutoCadSessionPayloadParser.IsSessionMode(Json(new { mode = "batch" })));
        Assert.False(AutoCadSessionPayloadParser.IsSessionMode(Json(new { autoCadExePath = @"C:\acad.exe" })));
        Assert.False(AutoCadSessionPayloadParser.IsSessionMode(Json(new object[] { 1, 2 })));
    }

    [Fact]
    public void Parser_Ping_MinimalPayload()
    {
        var ok = AutoCadSessionPayloadParser.TryParse(
            Json(new { mode = "session", action = "ping", sessionKey = "acad-1" }),
            out var request,
            out var error);

        Assert.True(ok, error);
        Assert.Equal(AutoCadSessionAction.Ping, request!.Action);
        Assert.Equal("acad-1", request.SessionKey);
        Assert.Empty(request.Entities);
        Assert.Empty(request.Handles);
    }

    [Fact]
    public void Parser_MissingAction_Fails()
    {
        var ok = AutoCadSessionPayloadParser.TryParse(Json(new { mode = "session" }), out _, out var error);

        Assert.False(ok);
        Assert.Contains("action", error);
    }

    [Fact]
    public void Parser_UnknownAction_Fails()
    {
        var ok = AutoCadSessionPayloadParser.TryParse(
            Json(new { mode = "session", action = "explode" }), out _, out var error);

        Assert.False(ok);
        Assert.Contains("explode", error);
    }

    [Fact]
    public void Parser_DrawEntities_ParsesAllEntityTypes()
    {
        var payload = Json(new
        {
            mode = "session",
            action = "drawEntities",
            entities = new object[]
            {
                new { type = "line", x1 = 0.0, y1 = 1.0, x2 = 2.0, y2 = 3.0, z2 = 4.0 },
                new { type = "polyline", points = new[] { new[] { 0.0, 0.0 }, new[] { 10.0, 0.0, 5.0 }, new[] { 10.0, 10.0 } }, closed = true },
                new { type = "circle", cx = 1.0, cy = 2.0, cz = 3.0, r = 4.0 },
                new { type = "text", x = 1.0, y = 2.0, height = 2.5, value = "Platform" }
            }
        });

        var ok = AutoCadSessionPayloadParser.TryParse(payload, out var request, out var error);

        Assert.True(ok, error);
        Assert.Equal(AutoCadSessionAction.DrawEntities, request!.Action);
        Assert.Equal(4, request.Entities.Count);

        var line = Assert.IsType<AutoCadLineSpec>(request.Entities[0]);
        Assert.Equal(0, line.Z1); // z1 не задан → 0
        Assert.Equal(4.0, line.Z2);

        var polyline = Assert.IsType<AutoCadPolylineSpec>(request.Entities[1]);
        Assert.True(polyline.Closed);
        Assert.Equal(3, polyline.Points.Count);

        var circle = Assert.IsType<AutoCadCircleSpec>(request.Entities[2]);
        Assert.Equal(4.0, circle.R);

        var text = Assert.IsType<AutoCadTextSpec>(request.Entities[3]);
        Assert.Equal("Platform", text.Value);
        Assert.Equal(0, text.Z);
    }

    [Fact]
    public void Parser_DrawEntities_EmptyEntities_Fails()
    {
        var ok = AutoCadSessionPayloadParser.TryParse(
            Json(new { mode = "session", action = "drawEntities", entities = Array.Empty<object>() }),
            out _, out var error);

        Assert.False(ok);
        Assert.Contains("entities", error);
    }

    [Fact]
    public void Parser_DrawEntities_UnknownEntityType_Fails()
    {
        var ok = AutoCadSessionPayloadParser.TryParse(
            Json(new
            {
                mode = "session",
                action = "drawEntities",
                entities = new object[] { new { type = "spline", x = 1.0 } }
            }),
            out _, out var error);

        Assert.False(ok);
        Assert.Contains("spline", error);
    }

    [Fact]
    public void Parser_Polyline_RequiresTwoPoints()
    {
        var ok = AutoCadSessionPayloadParser.TryParse(
            Json(new
            {
                mode = "session",
                action = "drawEntities",
                entities = new object[] { new { type = "polyline", points = new[] { new[] { 0.0, 0.0 } } } }
            }),
            out _, out var error);

        Assert.False(ok);
        Assert.Contains("polyline", error);
    }

    [Fact]
    public void Parser_Polyline_RejectsBadPointDimension()
    {
        var ok = AutoCadSessionPayloadParser.TryParse(
            Json(new
            {
                mode = "session",
                action = "drawEntities",
                entities = new object[]
                {
                    new { type = "polyline", points = new[] { new[] { 0.0 }, new[] { 1.0, 1.0 } } }
                }
            }),
            out _, out var error);

        Assert.False(ok);
        Assert.Contains("points[0]", error);
    }

    [Fact]
    public void Parser_Circle_RejectsNonPositiveRadius()
    {
        var ok = AutoCadSessionPayloadParser.TryParse(
            Json(new
            {
                mode = "session",
                action = "drawEntities",
                entities = new object[] { new { type = "circle", cx = 0.0, cy = 0.0, r = 0.0 } }
            }),
            out _, out var error);

        Assert.False(ok);
        Assert.Contains("r must be > 0", error);
    }

    [Fact]
    public void Parser_Text_RequiresValueAndPositiveHeight()
    {
        var noValue = AutoCadSessionPayloadParser.TryParse(
            Json(new
            {
                mode = "session",
                action = "drawEntities",
                entities = new object[] { new { type = "text", x = 0.0, y = 0.0, height = 2.5 } }
            }),
            out _, out var errorNoValue);
        Assert.False(noValue);
        Assert.Contains("value", errorNoValue);

        var badHeight = AutoCadSessionPayloadParser.TryParse(
            Json(new
            {
                mode = "session",
                action = "drawEntities",
                entities = new object[] { new { type = "text", x = 0.0, y = 0.0, height = 0.0, value = "x" } }
            }),
            out _, out var errorBadHeight);
        Assert.False(badHeight);
        Assert.Contains("height", errorBadHeight);
    }

    [Fact]
    public void Parser_EraseByHandles_ParsesHandles()
    {
        var ok = AutoCadSessionPayloadParser.TryParse(
            Json(new { mode = "session", action = "eraseByHandles", handles = new[] { "2A4", " 2A5 " } }),
            out var request, out var error);

        Assert.True(ok, error);
        Assert.Equal(AutoCadSessionAction.EraseByHandles, request!.Action);
        Assert.Equal(["2A4", "2A5"], request.Handles);
    }

    [Fact]
    public void Parser_EraseByHandles_RequiresHandles()
    {
        var ok = AutoCadSessionPayloadParser.TryParse(
            Json(new { mode = "session", action = "eraseByHandles" }), out _, out var error);

        Assert.False(ok);
        Assert.Contains("handles", error);
    }

    [Fact]
    public void Parser_ZoomExtents_NoExtraFieldsRequired()
    {
        var ok = AutoCadSessionPayloadParser.TryParse(
            Json(new { mode = "session", action = "zoomExtents" }), out var request, out var error);

        Assert.True(ok, error);
        Assert.Equal(AutoCadSessionAction.ZoomExtents, request!.Action);
    }

    // ────────── AutoCadEntityVariantConverter ──────────

    [Fact]
    public void Converter_Line_ProducesStartAndEndPoints()
    {
        var line = new AutoCadLineSpec(1, 2, 3, 4, 5, 6);

        Assert.Equal([1.0, 2.0, 3.0], AutoCadEntityVariantConverter.LineStart(line));
        Assert.Equal([4.0, 5.0, 6.0], AutoCadEntityVariantConverter.LineEnd(line));
    }

    [Fact]
    public void Converter_Polyline_FlattensTo3d_WithZeroZFor2dPoints()
    {
        var polyline = new AutoCadPolylineSpec(
            [
                new double[] { 0, 0 },
                new double[] { 10, 0, 5 },
                new double[] { 10, 20 }
            ],
            Closed: true);

        var flat = AutoCadEntityVariantConverter.PolylineFlat3d(polyline);

        Assert.Equal([0, 0, 0, 10, 0, 5, 10, 20, 0], flat);
    }

    [Fact]
    public void Converter_Polyline_ThrowsOnBadDimensions()
    {
        var tooFewPoints = new AutoCadPolylineSpec([new double[] { 0, 0 }], false);
        Assert.Throws<ArgumentException>(() => AutoCadEntityVariantConverter.PolylineFlat3d(tooFewPoints));

        var badPoint = new AutoCadPolylineSpec([new double[] { 0, 0 }, new double[] { 1 }], false);
        Assert.Throws<ArgumentException>(() => AutoCadEntityVariantConverter.PolylineFlat3d(badPoint));
    }

    [Fact]
    public void Converter_CircleAndText_Points()
    {
        Assert.Equal([7.0, 8.0, 9.0], AutoCadEntityVariantConverter.CircleCenter(new AutoCadCircleSpec(7, 8, 9, 1)));
        Assert.Equal([1.0, 2.0, 3.0], AutoCadEntityVariantConverter.TextInsertionPoint(new AutoCadTextSpec(1, 2, 3, 2.5, "t")));
    }

    // ────────── AutoCadRunningObjectTable: фильтр моникеров ──────────
    // (pure-части ROT-слоя; сам walk требует Windows/COM и проверяется live-probe'ом)

    [Fact]
    public void RotFilter_MatchesLegacyProgIdMonikers()
    {
        Assert.True(AutoCadRunningObjectTable.IsAutoCadApplicationDisplayName("!AutoCAD.Application.18:4321"));
        Assert.True(AutoCadRunningObjectTable.IsAutoCadApplicationDisplayName("!AutoCAD.Application.24.1"));
        Assert.True(AutoCadRunningObjectTable.IsAutoCadApplicationDisplayName("!autocad.application"));
    }

    [Fact]
    public void RotFilter_MatchesClsidMonikers_ViaProgIdResolver()
    {
        // Современный AutoCAD (проверено на 2022/R24.1): RegisterActiveObject
        // кладёт в ROT «!{CLSID}»; CLSID → ProgID резолвится через реестр
        // (здесь — инжектированный резолвер, чтобы тест не зависел от HKCR).
        var autoCadClsid = "AA46BA8A-9825-40FD-8493-0BA3C4D5CEB5";
        string? Resolver(string clsid) =>
            string.Equals(clsid, autoCadClsid, StringComparison.OrdinalIgnoreCase)
                ? "AutoCAD.Application.24.1"
                : null;

        Assert.True(AutoCadRunningObjectTable.IsAutoCadApplicationDisplayName(
            "!{AA46BA8A-9825-40FD-8493-0BA3C4D5CEB5}", Resolver));
        Assert.False(AutoCadRunningObjectTable.IsAutoCadApplicationDisplayName(
            "!{11111111-2222-3333-4444-555555555555}", Resolver));
    }

    [Fact]
    public void RotFilter_RejectsForeignMonikers()
    {
        string? NullResolver(string _) => null;

        Assert.False(AutoCadRunningObjectTable.IsAutoCadApplicationDisplayName(null, NullResolver));
        Assert.False(AutoCadRunningObjectTable.IsAutoCadApplicationDisplayName("", NullResolver));
        Assert.False(AutoCadRunningObjectTable.IsAutoCadApplicationDisplayName(
            "!Personal-Monikers::SyncEngineCOMServer", NullResolver));
        Assert.False(AutoCadRunningObjectTable.IsAutoCadApplicationDisplayName(
            @"C:\docs\file.xlsx", NullResolver));
    }

    [Fact]
    public void RotFilter_TryExtractClsid_ParsesBangBraceFormat()
    {
        var clsid = AutoCadRunningObjectTable.TryExtractClsid("!{AA46BA8A-9825-40FD-8493-0BA3C4D5CEB5}");
        Assert.Equal(Guid.Parse("AA46BA8A-9825-40FD-8493-0BA3C4D5CEB5"), clsid);

        Assert.Null(AutoCadRunningObjectTable.TryExtractClsid("!AutoCAD.Application.24.1"));
        Assert.Null(AutoCadRunningObjectTable.TryExtractClsid("!{not-a-guid}"));
        Assert.Null(AutoCadRunningObjectTable.TryExtractClsid(null));
    }

    [Fact]
    public void RotFilter_TryParsePidSuffix()
    {
        Assert.Equal(4321, AutoCadRunningObjectTable.TryParseProcessIdFromDisplayName("!AutoCAD.Application.18:4321"));
        Assert.Equal(0, AutoCadRunningObjectTable.TryParseProcessIdFromDisplayName("!AutoCAD.Application.24.1"));
        // GUID-моникер: двоеточий нет → 0 (PID добывается через HWND).
        Assert.Equal(0, AutoCadRunningObjectTable.TryParseProcessIdFromDisplayName("!{AA46BA8A-9825-40FD-8493-0BA3C4D5CEB5}"));
        Assert.Equal(0, AutoCadRunningObjectTable.TryParseProcessIdFromDisplayName("!x:"));
    }

    // ────────── ComRetryPolicy ──────────

    [Fact]
    public void RetryPolicy_RecognizesBusyHResults()
    {
        Assert.True(ComRetryPolicy.IsBusyHResult(ComRetryPolicy.RpcECallRejected));
        Assert.True(ComRetryPolicy.IsBusyHResult(ComRetryPolicy.RpcEServerCallRetryLater));
        Assert.False(ComRetryPolicy.IsBusyHResult(unchecked((int)0x80004005))); // E_FAIL
        Assert.False(ComRetryPolicy.IsBusyHResult(0));
    }

    [Fact]
    public void RetryPolicy_RetriesBusyThenSucceeds()
    {
        var attempts = 0;
        var delays = new List<int>();

        var result = ComRetryPolicy.Execute(
            () =>
            {
                attempts++;
                if (attempts < 3)
                {
                    throw new COMException("busy", ComRetryPolicy.RpcEServerCallRetryLater);
                }

                return "ok";
            },
            maxAttempts: 5,
            baseDelayMs: 10,
            delay: delays.Add);

        Assert.Equal("ok", result);
        Assert.Equal(3, attempts);
        Assert.Equal([10, 20], delays); // линейный backoff: base*1, base*2
    }

    [Fact]
    public void RetryPolicy_ExhaustsAttempts_ThrowsBusyException()
    {
        var attempts = 0;

        var ex = Assert.Throws<COMException>(() => ComRetryPolicy.Execute<string>(
            () =>
            {
                attempts++;
                throw new COMException("busy", ComRetryPolicy.RpcECallRejected);
            },
            maxAttempts: 3,
            baseDelayMs: 1,
            delay: _ => { }));

        Assert.Equal(3, attempts);
        Assert.True(ComRetryPolicy.IsBusyException(ex));
    }

    [Fact]
    public void RetryPolicy_NonBusyException_NoRetry()
    {
        var attempts = 0;

        Assert.Throws<InvalidOperationException>(() => ComRetryPolicy.Execute<string>(
            () =>
            {
                attempts++;
                throw new InvalidOperationException("hard failure");
            },
            maxAttempts: 5,
            baseDelayMs: 1,
            delay: _ => { }));

        Assert.Equal(1, attempts);
    }

    // ────────── ConnectorCapabilities ──────────

    [Fact]
    public void Capabilities_Apply_AddsOnceAndPreservesBase()
    {
        var result = ConnectorCapabilities.Apply(
            ["autocad.build", "tekla.apply", "AUTOCAD.SESSION"],
            ConnectorCapabilities.AutoCadSession,
            present: true);

        Assert.Equal(["autocad.build", "tekla.apply", "autocad.session"], result);
    }

    [Fact]
    public void Capabilities_Apply_Removes()
    {
        var result = ConnectorCapabilities.Apply(
            ["autocad.build", "autocad.session", "tekla.apply"],
            ConnectorCapabilities.AutoCadSession,
            present: false);

        Assert.Equal(["autocad.build", "tekla.apply"], result);
    }

    [Fact]
    public void Capabilities_ApplyDynamic_TogglesAndReportsChange()
    {
        var options = new ConnectorRuntimeOptions { Capabilities = ["autocad.build"] };

        Assert.True(ConnectorCapabilities.ApplyDynamic(options, ConnectorCapabilities.AutoCadSession, true));
        Assert.Contains("autocad.session", options.Capabilities);

        // Повторное применение того же состояния — без изменений.
        Assert.False(ConnectorCapabilities.ApplyDynamic(options, ConnectorCapabilities.AutoCadSession, true));

        Assert.True(ConnectorCapabilities.ApplyDynamic(options, ConnectorCapabilities.AutoCadSession, false));
        Assert.DoesNotContain("autocad.session", options.Capabilities);

        Assert.False(ConnectorCapabilities.ApplyDynamic(null, ConnectorCapabilities.AutoCadSession, true));
    }

    [Fact]
    public void DesktopSettings_AutoCadSessionKey_RoundTripsAndTrims()
    {
        var root = Path.Combine(Path.GetTempPath(), "connector-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var service = new DesktopSettingsService(root);
            var settings = service.Load();
            settings.AutoCadSessionKey = "  acad-123  ";
            service.Save(settings);

            var reloaded = service.Load();
            Assert.Equal("acad-123", reloaded.AutoCadSessionKey);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }
}
