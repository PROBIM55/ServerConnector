// Wave B: роутинг AutoCadProviderAdapter — mode=session идёт в session-исполнитель
// (COM подменён fake'ом), всё остальное — прежний batch-путь без изменений.

using System.Text.Json;
using Platform.Cad.AutoCad.Adapter;
using Platform.Cad.AutoCad.Adapter.Sessions;
using Platform.Connector.Core;

namespace Platform.Connector.Core.Tests;

public sealed class AutoCadSessionRoutingTests
{
    private static JsonElement Json(object src)
        => JsonDocument.Parse(JsonSerializer.Serialize(src)).RootElement.Clone();

    private static ConnectorJobEnvelope CreateJob(string requestId, JsonElement payload)
        => new(
            SchemaVersion: 1,
            RequestId: requestId,
            ModuleId: "bridge",
            Provider: CadProvider.AutoCad,
            Operation: JobOperation.Build,
            CreatedAtUtc: DateTime.UtcNow,
            Payload: payload,
            CorrelationId: $"corr-{requestId}");

    [Fact]
    public async Task SessionMode_RoutesToSessionExecutor()
    {
        var fake = new FakeSessionExecutor
        {
            Result = new AutoCadSessionOperationResult(
                true,
                Message: "pong",
                Session: NewSession(123),
                Ping: new AutoCadPingInfo("24.1", "Чертёж1.dwg", @"C:\dwg\Чертёж1.dwg", 1, 5))
        };
        var adapter = new AutoCadProviderAdapter(() => fake);

        var payload = Json(new { mode = "session", action = "ping", sessionKey = "acad-123" });
        var result = await adapter.ExecuteAsync(CreateJob("session-ping", payload), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Message);
        Assert.NotNull(fake.LastRequest);
        Assert.Equal(AutoCadSessionAction.Ping, fake.LastRequest!.Action);
        Assert.Equal("acad-123", fake.LastRequest.SessionKey);

        var json = result.Result!.Value;
        Assert.Equal("session", json.GetProperty("mode").GetString());
        Assert.Equal("ping", json.GetProperty("action").GetString());
        Assert.Equal("acad-123", json.GetProperty("sessionKey").GetString());
        Assert.Equal(5, json.GetProperty("ping").GetProperty("modelSpaceCount").GetInt32());
    }

    [Fact]
    public async Task LegacyPayload_DoesNotTouchSessionExecutor()
    {
        var fake = new FakeSessionExecutor();
        var adapter = new AutoCadProviderAdapter(() => fake);

        // Прежний batch-контракт: без autoCadExePath падает AUTOCAD_EXE_REQUIRED —
        // поведение не изменилось, session-исполнитель не вызывался.
        var payload = Json(new { workingDirectory = Path.Combine(Path.GetTempPath(), "acad-batch-" + Guid.NewGuid().ToString("N")) });
        var result = await adapter.ExecuteAsync(CreateJob("legacy-batch", payload), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("AUTOCAD_EXE_REQUIRED", result.ErrorCode);
        Assert.Null(fake.LastRequest);
        Assert.Equal(0, fake.CallCount);
    }

    [Fact]
    public async Task SessionMode_InvalidPayload_FailsWithoutExecutorCall()
    {
        var fake = new FakeSessionExecutor();
        var adapter = new AutoCadProviderAdapter(() => fake);

        var payload = Json(new { mode = "session", action = "drawEntities" }); // нет entities
        var result = await adapter.ExecuteAsync(CreateJob("session-bad-payload", payload), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(AutoCadSessionErrorCodes.PayloadInvalid, result.ErrorCode);
        Assert.Equal(0, fake.CallCount);
    }

    [Fact]
    public async Task SessionMode_DrawEntities_ReturnsHandlesAndSessionKey()
    {
        var fake = new FakeSessionExecutor
        {
            Result = new AutoCadSessionOperationResult(
                true,
                Message: "Создано объектов: 4.",
                Session: NewSession(777),
                CreatedHandles: ["2A4", "2A5", "2A6", "2A7"])
        };
        var adapter = new AutoCadProviderAdapter(() => fake);

        var payload = Json(new
        {
            mode = "session",
            action = "drawEntities",
            entities = new object[]
            {
                new { type = "line", x1 = 0.0, y1 = 0.0, x2 = 1000.0, y2 = 0.0 },
                new { type = "line", x1 = 1000.0, y1 = 0.0, x2 = 1000.0, y2 = 500.0 },
                new { type = "line", x1 = 1000.0, y1 = 500.0, x2 = 0.0, y2 = 500.0 },
                new { type = "line", x1 = 0.0, y1 = 500.0, x2 = 0.0, y2 = 0.0 }
            }
        });
        var result = await adapter.ExecuteAsync(CreateJob("session-draw", payload), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(4, fake.LastRequest!.Entities.Count);
        Assert.All(fake.LastRequest.Entities, e => Assert.IsType<AutoCadLineSpec>(e));

        var json = result.Result!.Value;
        Assert.Equal("acad-777", json.GetProperty("sessionKey").GetString());
        Assert.Equal(4, json.GetProperty("handles").GetArrayLength());
        Assert.Equal("2A4", json.GetProperty("handles")[0].GetString());
    }

    [Fact]
    public async Task SessionMode_ExecutorFailure_MapsErrorCode()
    {
        var fake = new FakeSessionExecutor
        {
            Result = new AutoCadSessionOperationResult(
                false,
                AutoCadSessionErrorCodes.SessionNotSelected,
                "2 AutoCAD sessions are running and none is selected.")
        };
        var adapter = new AutoCadProviderAdapter(() => fake);

        var payload = Json(new { mode = "session", action = "ping" });
        var result = await adapter.ExecuteAsync(CreateJob("session-not-selected", payload), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(AutoCadSessionErrorCodes.SessionNotSelected, result.ErrorCode);
        Assert.Contains("none is selected", result.Message);
    }

    [Fact]
    public async Task SessionMode_ExecutorThrows_MapsToComError()
    {
        var fake = new FakeSessionExecutor { ThrowOnExecute = new InvalidOperationException("COM exploded") };
        var adapter = new AutoCadProviderAdapter(() => fake);

        var payload = Json(new { mode = "session", action = "zoomExtents" });
        var result = await adapter.ExecuteAsync(CreateJob("session-throw", payload), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(AutoCadSessionErrorCodes.ComError, result.ErrorCode);
        Assert.Contains("COM exploded", result.Message);
    }

    [Fact]
    public async Task SessionMode_EraseByHandles_PassesHandlesThrough()
    {
        var fake = new FakeSessionExecutor
        {
            Result = new AutoCadSessionOperationResult(
                true,
                Message: "Удалено объектов: 2 из 3.",
                Session: NewSession(1),
                ErasedCount: 2,
                NotFoundHandles: ["FFFF"])
        };
        var adapter = new AutoCadProviderAdapter(() => fake);

        var payload = Json(new { mode = "session", action = "eraseByHandles", handles = new[] { "2A4", "2A5", "FFFF" } });
        var result = await adapter.ExecuteAsync(CreateJob("session-erase", payload), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(["2A4", "2A5", "FFFF"], fake.LastRequest!.Handles);

        var json = result.Result!.Value;
        Assert.Equal(2, json.GetProperty("erasedCount").GetInt32());
        Assert.Equal("FFFF", json.GetProperty("notFoundHandles")[0].GetString());
    }

    private static AutoCadSessionInfo NewSession(int pid)
        => new(AutoCadSessionKey.Format(pid), pid, "24.1", "Чертёж1.dwg", @"C:\dwg\Чертёж1.dwg", $"AutoCAD {pid}");

    private sealed class FakeSessionExecutor : IAutoCadSessionExecutor
    {
        public AutoCadSessionOperationResult Result { get; set; } =
            new(true, Message: "ok", Session: NewSession(1));

        public Exception? ThrowOnExecute { get; set; }

        public AutoCadSessionRequest? LastRequest { get; private set; }
        public int CallCount { get; private set; }

        public Task<IReadOnlyList<AutoCadSessionInfo>> ListSessionsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<AutoCadSessionInfo>>([NewSession(1)]);

        public Task<AutoCadSessionOperationResult> ExecuteAsync(AutoCadSessionRequest request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequest = request;
            if (ThrowOnExecute is not null)
            {
                throw ThrowOnExecute;
            }

            return Task.FromResult(Result);
        }
    }
}
