using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Platform.Cad.AutoCad.Adapter;
using Platform.Cad.Tekla.Adapter;
using Platform.Connector.Core;

namespace Platform.Connector.Core.Tests;

public sealed class CadProviderAdapterRuntimeTests
{
    [Fact]
    public async Task AutoCadAdapter_ShouldFail_WhenPluginIsMissing()
    {
        var sandbox = AutoCadSandbox.Create();
        var payload = JsonElementFromAnonymous(new { jobTimeoutSeconds = 5 });

        var result = await sandbox.Adapter.ExecuteAsync(CreateJob("autocad-missing-plugin", CadProvider.AutoCad, payload), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("PLUGIN_NOT_FOUND", result.ErrorCode);
        Assert.False(sandbox.ExeResolverCalled);
    }

    [Fact]
    public async Task AutoCadAdapter_ShouldReject_ScriptPathFromPayload()
    {
        var sandbox = AutoCadSandbox.Create();
        var payload = JsonElementFromAnonymous(new
        {
            autoCadExePath = sandbox.ExePath,
            scriptPath = Path.Combine(sandbox.Root, "evil.scr"),
            bridgePluginDllPath = sandbox.PluginPath
        });

        var result = await sandbox.Adapter.ExecuteAsync(CreateJob("autocad-script-path", CadProvider.AutoCad, payload), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(AutoCadProviderAdapter.PayloadPathRejectedErrorCode, result.ErrorCode);
        Assert.False(sandbox.ExeResolverCalled);
        Assert.False(Directory.Exists(sandbox.StateRoot));
    }

    [Theory]
    [InlineData(@"\\attacker\share\acad.exe")]
    [InlineData(@"\\?\C:\Windows\System32\cmd.exe")]
    [InlineData(@"acad.exe")]
    [InlineData(@"C:acad.exe")]
    public async Task AutoCadAdapter_ShouldReject_NonLocalExePathFromPayload(string exePath)
    {
        var sandbox = AutoCadSandbox.Create();
        var payload = JsonElementFromAnonymous(new
        {
            autoCadExePath = exePath,
            bridgePluginDllPath = sandbox.PluginPath
        });

        var result = await sandbox.Adapter.ExecuteAsync(CreateJob("autocad-exe-unc", CadProvider.AutoCad, payload), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(AutoCadProviderAdapter.PayloadPathRejectedErrorCode, result.ErrorCode);
    }

    [Fact]
    public async Task AutoCadAdapter_ShouldReject_ExePathThatDiffersFromLocalInstall_WithoutDisclosingIt()
    {
        var sandbox = AutoCadSandbox.Create();
        var payload = JsonElementFromAnonymous(new
        {
            autoCadExePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
            bridgePluginDllPath = sandbox.PluginPath
        });

        var result = await sandbox.Adapter.ExecuteAsync(CreateJob("autocad-exe-mismatch", CadProvider.AutoCad, payload), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(AutoCadProviderAdapter.PayloadPathRejectedErrorCode, result.ErrorCode);
        Assert.DoesNotContain("whoami", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(sandbox.StateRoot));
    }

    [Fact]
    public async Task AutoCadAdapter_ShouldFail_WhenLocalAutoCadIsNotInstalled()
    {
        var sandbox = AutoCadSandbox.Create(exeResolver: () => null);
        var payload = JsonElementFromAnonymous(new { bridgePluginDllPath = sandbox.PluginPath });

        var result = await sandbox.Adapter.ExecuteAsync(CreateJob("autocad-not-installed", CadProvider.AutoCad, payload), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("AUTOCAD_EXE_NOT_FOUND", result.ErrorCode);
    }

    [Theory]
    [InlineData("outside")]
    [InlineData("traversal")]
    [InlineData("unc")]
    [InlineData("not-dll")]
    public async Task AutoCadAdapter_ShouldReject_PluginOutsideInstallDirectory(string kind)
    {
        var sandbox = AutoCadSandbox.Create();
        var outsideDll = Path.Combine(sandbox.Root, "outside.dll");
        File.WriteAllBytes(outsideDll, [0x4D, 0x5A]);
        var pluginPath = kind switch
        {
            "outside" => outsideDll,
            "traversal" => Path.Combine(sandbox.PluginDirectory, "..", "outside.dll"),
            "unc" => @"\\attacker\share\plugin.dll",
            _ => Path.Combine(sandbox.PluginDirectory, "payload.js"),
        };
        var payload = JsonElementFromAnonymous(new { bridgePluginDllPath = pluginPath });

        var result = await sandbox.Adapter.ExecuteAsync(CreateJob($"autocad-plugin-{kind}", CadProvider.AutoCad, payload), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(AutoCadProviderAdapter.PayloadPathRejectedErrorCode, result.ErrorCode);
        Assert.DoesNotContain(sandbox.Root, result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(sandbox.ExeResolverCalled);
    }

    [Theory]
    [InlineData("workingDirectory", "unc")]
    [InlineData("workingDirectory", "world-writable")]
    [InlineData("workingDirectory", "inside-root")]
    [InlineData("jobPointerPath", "user-file")]
    [InlineData("jobPointerPath", "traversal")]
    [InlineData("buildStatusPath", "user-file")]
    [InlineData("buildStatusPath", "inside-root")]
    [InlineData("buildStatusPath", "unc")]
    public async Task AutoCadAdapter_ShouldReject_StatePathOverridesFromPayload(string field, string kind)
    {
        var sandbox = AutoCadSandbox.Create();
        var value = kind switch
        {
            "unc" => @"\\attacker\share\x.json",
            "world-writable" => Path.Combine(Path.GetTempPath(), "shared-run"),
            "inside-root" => Path.Combine(sandbox.StateRoot, "build-status.json"),
            "traversal" => Path.Combine(sandbox.StateRoot, "..", "..", "victim.json"),
            _ => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".docker", "config.json"),
        };
        var values = new Dictionary<string, object>
        {
            ["bridgePluginDllPath"] = sandbox.PluginPath,
            [field] = value,
        };

        var result = await sandbox.Adapter.ExecuteAsync(
            CreateJob($"autocad-state-{field}-{kind}", CadProvider.AutoCad, JsonElementFromAnonymous(values)),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(AutoCadProviderAdapter.PayloadPathRejectedErrorCode, result.ErrorCode);
        Assert.False(sandbox.ExeResolverCalled);
        Assert.False(Directory.Exists(sandbox.StateRoot));
    }

    [Fact]
    public async Task AutoCadAdapter_ShouldReject_UncJobPath()
    {
        var sandbox = AutoCadSandbox.Create();
        var payload = JsonElementFromAnonymous(new
        {
            bridgePluginDllPath = sandbox.PluginPath,
            jobPath = @"\\attacker\share\job.json"
        });

        var result = await sandbox.Adapter.ExecuteAsync(CreateJob("autocad-unc-job", CadProvider.AutoCad, payload), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(AutoCadProviderAdapter.PayloadPathRejectedErrorCode, result.ErrorCode);
        Assert.False(sandbox.ExeResolverCalled);
    }

    [Theory]
    [InlineData("BRIDGE_BUILD\nSHELL calc")]
    [InlineData("BRIDGE_BUILD\n")]
    [InlineData("(command \"SHELL\")")]
    [InlineData("1BUILD")]
    [InlineData("BUILD COMMAND")]
    public async Task AutoCadAdapter_ShouldReject_UnsafeCommandName(string commandName)
    {
        var sandbox = AutoCadSandbox.Create();
        var payload = JsonElementFromAnonymous(new
        {
            bridgePluginDllPath = sandbox.PluginPath,
            commandName
        });

        var result = await sandbox.Adapter.ExecuteAsync(CreateJob("autocad-command-name", CadProvider.AutoCad, payload), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(AutoCadProviderAdapter.CommandNameRejectedErrorCode, result.ErrorCode);
        Assert.False(sandbox.ExeResolverCalled);
    }

    [Fact]
    public async Task AutoCadAdapter_ShouldReject_LocalStateRootJunction()
    {
        var sandbox = AutoCadSandbox.Create();
        var victim = Path.Combine(sandbox.Root, "victim");
        Directory.CreateDirectory(victim);
        using (var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
               {
                   FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
                   ArgumentList = { "/c", "mklink", "/J", sandbox.StateRoot, victim },
                   UseShellExecute = false,
                   CreateNoWindow = true,
                   RedirectStandardOutput = true,
               })!)
        {
            mklink.WaitForExit();
            Assert.Equal(0, mklink.ExitCode);
        }

        var payload = JsonElementFromAnonymous(new { bridgePluginDllPath = sandbox.PluginPath, jobTimeoutSeconds = 5 });

        var result = await sandbox.Adapter.ExecuteAsync(CreateJob("autocad-state-junction", CadProvider.AutoCad, payload), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(AutoCadProviderAdapter.LocalStateUnsafeErrorCode, result.ErrorCode);
        Assert.Empty(Directory.EnumerateFileSystemEntries(victim));
    }

    [Fact]
    public async Task AutoCadAdapter_ShouldRunInstalledPlugin_WithLocallyGeneratedScript()
    {
        var sandbox = AutoCadSandbox.Create();
        Directory.CreateDirectory(sandbox.StateRoot);
        File.WriteAllText(Path.Combine(sandbox.StateRoot, "build-status.json"), """{"status":"success","message":"simulated"}""");
        var payload = JsonElementFromAnonymous(new
        {
            autoCadExePath = sandbox.ExePath,
            bridgePluginDllPath = sandbox.PluginPath,
            commandName = "_BRIDGE_BUILD_LAST_V2",
            jobTimeoutSeconds = 15
        });

        var result = await sandbox.Adapter.ExecuteAsync(CreateJob("autocad-installed-plugin", CadProvider.AutoCad, payload), CancellationToken.None);

        Assert.True(result.IsSuccess, $"expected success, got {result.ErrorCode}: {result.Message}");
        var runDirectory = Assert.Single(Directory.GetDirectories(Path.Combine(sandbox.StateRoot, "runs")));
        var script = File.ReadAllText(Path.Combine(runDirectory, "run.scr"));
        Assert.DoesNotContain("SECURELOAD", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"\"{sandbox.PluginPath}\"", script);
        Assert.Contains("_BRIDGE_BUILD_LAST_V2", script);
    }

    // ────────── Tekla: HTTP forwarder contract (Phase 5) ──────────

    [Fact]
    public async Task TeklaAdapter_ShouldFail_WhenEndpointMissing()
    {
        var adapter = NewAdapterWithFakeBridge(new FakeBridgeHandler());

        var payload = JsonElementFromAnonymous(new { body = new { } });
        var job = CreateJob("missing-endpoint", CadProvider.Tekla, payload);

        var result = await adapter.ExecuteAsync(job, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("BRIDGE_PAYLOAD_INVALID", result.ErrorCode);
    }

    [Fact]
    public async Task TeklaAdapter_ShouldFail_WhenTokenMissing()
    {
        var adapter = new TeklaProviderAdapter(
            tokenLoader: _ => null,
            httpClientFactory: _ => new HttpClient(new FakeBridgeHandler()));

        var payload = JsonElementFromAnonymous(new
        {
            endpoint = "/component/upsert",
            body = new { componentType = "BridgeGirder" }
        });
        var job = CreateJob("missing-token", CadProvider.Tekla, payload);

        var result = await adapter.ExecuteAsync(job, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("BRIDGE_TOKEN_UNAVAILABLE", result.ErrorCode);
    }

    [Fact]
    public async Task TeklaAdapter_ShouldFail_WhenBridgeUnreachable()
    {
        var handler = new FakeBridgeHandler { HealthStatus = HttpStatusCode.ServiceUnavailable };
        var adapter = NewAdapterWithFakeBridge(handler);

        var payload = JsonElementFromAnonymous(new
        {
            endpoint = "/component/upsert",
            body = new { componentType = "BridgeGirder" }
        });
        var job = CreateJob("bridge-down", CadProvider.Tekla, payload);

        var result = await adapter.ExecuteAsync(job, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("BRIDGE_DESKTOP_UNREACHABLE", result.ErrorCode);
    }

    [Fact]
    public async Task TeklaAdapter_ShouldForwardBody_AndAttachToken()
    {
        var handler = new FakeBridgeHandler();
        var adapter = NewAdapterWithFakeBridge(handler, token: "my-token-xyz");

        var bodyObj = new { componentType = "BridgeGirder", schemaVersion = 1, externalObjectId = "ext-1" };
        var payload = JsonElementFromAnonymous(new
        {
            endpoint = "/component/upsert",
            body = bodyObj,
            diagnostics = true
        });
        var job = CreateJob("forward-success", CadProvider.Tekla, payload);

        var result = await adapter.ExecuteAsync(job, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(handler.LastRequest);
        Assert.Equal("/component/upsert", handler.LastRequest!.Path);
        Assert.Equal("my-token-xyz", handler.LastRequest!.Token);
        Assert.Contains("\"BridgeGirder\"", handler.LastRequest.Body);
        Assert.Contains("\"ext-1\"", handler.LastRequest.Body);
    }

    [Fact]
    public async Task TeklaAdapter_ShouldMapErrorCode_FromBridgeJson()
    {
        var handler = new FakeBridgeHandler
        {
            ResponseStatus = HttpStatusCode.NotFound,
            ResponseBody = """{"ok":false,"errorCode":"TEKLA_OBJECT_NOT_FOUND","message":"not in model"}""",
        };
        var adapter = NewAdapterWithFakeBridge(handler);

        var payload = JsonElementFromAnonymous(new
        {
            endpoint = "/component/modify",
            body = new { externalObjectId = "missing-id" }
        });
        var job = CreateJob("bridge-error-mapping", CadProvider.Tekla, payload);

        var result = await adapter.ExecuteAsync(job, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("TEKLA_OBJECT_NOT_FOUND", result.ErrorCode);
    }

    [Fact]
    public async Task TeklaAdapter_ShouldHardRestart_OnBridgeStaleTekla_ThenRetry()
    {
        // Bridge.Desktop отвечает 503/BRIDGE_STALE_TEKLA на первый вызов
        // (внутренний reconnect не помог) и 200 OK после рестарта.
        // Connector должен вызвать restarter и повторить запрос один раз.
        var handler = new FakeBridgeHandler();
        handler.QueueResponse(HttpStatusCode.ServiceUnavailable,
            """{"ok":false,"errorCode":"BRIDGE_STALE_TEKLA","message":"stale handles"}""");
        handler.QueueResponse(HttpStatusCode.OK, """{"ok":true,"recovered":true}""");

        var restarter = new FakeRestarter { IsAvailable = true, ReturnValue = true };
        var adapter = new TeklaProviderAdapter(
            tokenLoader: _ => "tok",
            httpClientFactory: _ => new HttpClient(handler),
            restarter: restarter);

        var payload = JsonElementFromAnonymous(new
        {
            endpoint = "/component/upsert",
            body = new { externalObjectId = "ext-1" }
        });
        var job = CreateJob("stale-recovery", CadProvider.Tekla, payload);

        var result = await adapter.ExecuteAsync(job, CancellationToken.None);

        Assert.True(result.IsSuccess, $"expected recovered success, got {result.ErrorCode}");
        Assert.Equal(1, restarter.CallCount);
        Assert.Equal(2, handler.PostCallCount); // первый 503 + retry 200
    }

    [Fact]
    public async Task TeklaAdapter_ShouldNotRetry_WhenRestarterUnavailable()
    {
        // На non-Windows / в тестах с NoopRestarter — recovery не делается,
        // результат BRIDGE_STALE_TEKLA пробрасывается наружу без retry.
        var handler = new FakeBridgeHandler
        {
            ResponseStatus = HttpStatusCode.ServiceUnavailable,
            ResponseBody = """{"ok":false,"errorCode":"BRIDGE_STALE_TEKLA","message":"stale"}""",
        };
        var restarter = new FakeRestarter { IsAvailable = false };
        var adapter = new TeklaProviderAdapter(
            tokenLoader: _ => "tok",
            httpClientFactory: _ => new HttpClient(handler),
            restarter: restarter);

        var payload = JsonElementFromAnonymous(new
        {
            endpoint = "/component/upsert",
            body = new { externalObjectId = "ext-1" }
        });
        var job = CreateJob("stale-no-restarter", CadProvider.Tekla, payload);

        var result = await adapter.ExecuteAsync(job, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("BRIDGE_STALE_TEKLA", result.ErrorCode);
        Assert.Equal(0, restarter.CallCount);
        Assert.Equal(1, handler.PostCallCount);
    }

    [Fact]
    public async Task TeklaAdapter_ShouldRestartVersionedBridgeProfile_WhenTeklaHandleIsStale()
    {
        var handler = new FakeBridgeHandler
        {
            ResponseStatus = HttpStatusCode.ServiceUnavailable,
            ResponseBody = """{"ok":false,"errorCode":"BRIDGE_STALE_TEKLA","message":"stale"}""",
        };
        var restarter = new FakeRestarter { IsAvailable = true, ReturnValue = true };
        var adapter = new TeklaProviderAdapter(
            tokenLoader: _ => "tok",
            httpClientFactory: _ => new HttpClient(handler),
            restarter: restarter);

        var payload = JsonElementFromAnonymous(new
        {
            endpoint = "/component/upsert",
            bridgeBaseUrl = "http://127.0.0.1:39420",
            body = new { externalObjectId = "ext-1" }
        });
        var job = CreateJob("stale-versioned-profile", CadProvider.Tekla, payload);

        var result = await adapter.ExecuteAsync(job, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("BRIDGE_STALE_TEKLA", result.ErrorCode);
        Assert.Equal(1, restarter.CallCount);
        Assert.Equal(2, handler.PostCallCount);
    }

    [Fact]
    public async Task TeklaAdapter_ShouldNotHardRestart_ForCapabilitiesProbe()
    {
        var handler = new FakeBridgeHandler
        {
            ResponseStatus = HttpStatusCode.ServiceUnavailable,
            ResponseBody = """{"ok":false,"errorCode":"BRIDGE_STALE_TEKLA","message":"stale"}""",
        };
        var restarter = new FakeRestarter { IsAvailable = true, ReturnValue = true };
        var adapter = new TeklaProviderAdapter(
            tokenLoader: _ => "tok",
            httpClientFactory: _ => new HttpClient(handler),
            restarter: restarter);

        var payload = JsonElementFromAnonymous(new
        {
            endpoint = "/capabilities",
            body = new { }
        });
        var job = CreateJob("stale-capabilities", CadProvider.Tekla, payload);

        var result = await adapter.ExecuteAsync(job, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("BRIDGE_STALE_TEKLA", result.ErrorCode);
        Assert.Equal(0, restarter.CallCount);
        Assert.Equal(1, handler.PostCallCount);
    }

    [Fact]
    public async Task TeklaAdapter_ShouldStartVersionedProfile_ForHealthProbeWhenItIsDown()
    {
        var handler = new FakeBridgeHandler { HealthStatus = HttpStatusCode.ServiceUnavailable };
        var restarter = new FakeRestarter
        {
            IsAvailable = true,
            ReturnValue = true,
            OnRestart = _ => handler.HealthStatus = HttpStatusCode.OK,
        };
        var adapter = new TeklaProviderAdapter(
            tokenLoader: _ => "tok",
            httpClientFactory: _ => new HttpClient(handler),
            restarter: restarter);
        var payload = JsonElementFromAnonymous(new
        {
            endpoint = "/health",
            bridgeBaseUrl = "http://127.0.0.1:39420",
            body = new { }
        });

        var result = await adapter.ExecuteAsync(
            CreateJob("health-start-versioned-profile", CadProvider.Tekla, payload),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, restarter.CallCount);
    }

    [Fact]
    public async Task TeklaAdapter_ShouldNotStartDefaultProfile_ForHealthProbeWhenItIsDown()
    {
        var handler = new FakeBridgeHandler { HealthStatus = HttpStatusCode.ServiceUnavailable };
        var restarter = new FakeRestarter { IsAvailable = true, ReturnValue = true };
        var adapter = new TeklaProviderAdapter(
            tokenLoader: _ => "tok",
            httpClientFactory: _ => new HttpClient(handler),
            restarter: restarter);
        var payload = JsonElementFromAnonymous(new
        {
            endpoint = "/health",
            bridgeBaseUrl = TeklaProviderAdapter.DefaultBridgeBaseUrl,
            body = new { }
        });

        var result = await adapter.ExecuteAsync(
            CreateJob("health-do-not-start-default-profile", CadProvider.Tekla, payload),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("BRIDGE_DESKTOP_UNREACHABLE", result.ErrorCode);
        Assert.Equal(0, restarter.CallCount);
    }

    [Fact]
    public async Task TeklaAdapter_ShouldReturnFinalFailure_WhenRetryAlsoFails()
    {
        // Restart прошёл, но retry тоже падает (e.g. 503 второй раз) —
        // возвращаем последний результат без бесконечного цикла.
        var handler = new FakeBridgeHandler();
        handler.QueueResponse(HttpStatusCode.ServiceUnavailable,
            """{"ok":false,"errorCode":"BRIDGE_STALE_TEKLA","message":"stale1"}""");
        handler.QueueResponse(HttpStatusCode.ServiceUnavailable,
            """{"ok":false,"errorCode":"BRIDGE_STALE_TEKLA","message":"stale2"}""");
        var restarter = new FakeRestarter { IsAvailable = true, ReturnValue = true };
        var adapter = new TeklaProviderAdapter(
            tokenLoader: _ => "tok",
            httpClientFactory: _ => new HttpClient(handler),
            restarter: restarter);

        var payload = JsonElementFromAnonymous(new
        {
            endpoint = "/component/upsert",
            body = new { externalObjectId = "ext-1" }
        });
        var job = CreateJob("stale-retry-fails", CadProvider.Tekla, payload);

        var result = await adapter.ExecuteAsync(job, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("BRIDGE_STALE_TEKLA", result.ErrorCode);
        Assert.Equal(1, restarter.CallCount);
        Assert.Equal(2, handler.PostCallCount); // не уходим в бесконечный цикл
    }

    [Fact]
    public async Task TeklaAdapter_ShouldMapBridgeUnauthorized()
    {
        var handler = new FakeBridgeHandler { ResponseStatus = HttpStatusCode.Unauthorized, ResponseBody = "" };
        var adapter = NewAdapterWithFakeBridge(handler);

        var payload = JsonElementFromAnonymous(new
        {
            endpoint = "/component/upsert",
            body = new { x = 1 }
        });
        var job = CreateJob("bridge-401", CadProvider.Tekla, payload);

        var result = await adapter.ExecuteAsync(job, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("BRIDGE_UNAUTHORIZED", result.ErrorCode);
    }

    [Fact]
    public async Task TeklaAdapter_ShouldReadCapabilities_WithGet()
    {
        var handler = new FakeBridgeHandler();
        var adapter = NewAdapterWithFakeBridge(handler);
        var payload = JsonElementFromAnonymous(new
        {
            endpoint = "/capabilities",
            bridgeBaseUrl = "http://127.0.0.1:39420",
            body = new { }
        });
        var job = CreateJob("bridge-capabilities", CadProvider.Tekla, payload);

        var result = await adapter.ExecuteAsync(job, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(HttpMethod.Get, handler.LastRequest?.Method);
        Assert.Equal("/capabilities", handler.LastRequest?.Path);
    }

    [Theory]
    [InlineData("http://10.0.0.5:39421")]
    [InlineData("http://bimplatforma.example:39421")]
    [InlineData("https://127.0.0.1:39421")]
    [InlineData("http://127.0.0.1:39421@evil.example")]
    [InlineData("http://127.0.0.1@evil.example:39421")]
    [InlineData("http://127.0.0.1")]
    [InlineData("http://127.0.0.1:39421/proxy")]
    [InlineData("http://127.0.0.1:39421/?x=1")]
    [InlineData("file:///C:/token.dat")]
    [InlineData("not a url")]
    [InlineData("http://127.0.0.1:8080")]
    [InlineData("http://localhost:5068")]
    [InlineData("http://[::1]:39421")]
    public async Task TeklaAdapter_ShouldReject_NonLoopbackBridgeBaseUrl(string bridgeBaseUrl)
    {
        var handler = new FakeBridgeHandler();
        var tokenLoads = 0;
        var adapter = new TeklaProviderAdapter(
            tokenLoader: _ => { tokenLoads++; return "secret-token"; },
            httpClientFactory: _ => new HttpClient(handler));
        var payload = JsonElementFromAnonymous(new
        {
            endpoint = "/component/upsert",
            bridgeBaseUrl,
            body = new { x = 1 }
        });

        var result = await adapter.ExecuteAsync(CreateJob("bridge-url-rejected", CadProvider.Tekla, payload), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(TeklaProviderAdapter.BridgeUrlRejectedErrorCode, result.ErrorCode);
        Assert.Equal(0, tokenLoads);
        Assert.Null(handler.LastRequest);
    }

    [Theory]
    [InlineData("http://localhost:39420/", 39420)]
    [InlineData("http://127.0.0.1:39420", 39420)]
    [InlineData("http://LOCALHOST:39421", 39421)]
    public async Task TeklaAdapter_ShouldAccept_KnownBridgeBaseUrl_NormalizedTo127(string bridgeBaseUrl, int expectedPort)
    {
        var handler = new FakeBridgeHandler();
        var adapter = NewAdapterWithFakeBridge(handler);
        var payload = JsonElementFromAnonymous(new
        {
            endpoint = "/component/read",
            bridgeBaseUrl,
            body = new { externalObjectId = "ext-1" }
        });

        var result = await adapter.ExecuteAsync(CreateJob("bridge-url-loopback", CadProvider.Tekla, payload), CancellationToken.None);

        Assert.True(result.IsSuccess, $"expected success, got {result.ErrorCode}");
        Assert.Equal("127.0.0.1", handler.LastRequest?.Host);
        Assert.Equal(expectedPort, handler.LastRequest?.Port);
    }

    [Theory]
    [InlineData("/plugins/fachwerk/reload")]
    [InlineData("/plugins/fachwerk/columns/refresh")]
    [InlineData("/session/start")]
    [InlineData("/component/upsert?x=1")]
    [InlineData("/component/../plugins/fachwerk/reload")]
    [InlineData("/COMPONENT/UPSERT")]
    [InlineData("//evil.example/component/upsert")]
    public async Task TeklaAdapter_ShouldReject_EndpointOutsideAllowlist(string endpoint)
    {
        var handler = new FakeBridgeHandler();
        var tokenLoads = 0;
        var adapter = new TeklaProviderAdapter(
            tokenLoader: _ => { tokenLoads++; return "secret-token"; },
            httpClientFactory: _ => new HttpClient(handler));
        var payload = JsonElementFromAnonymous(new { endpoint, body = new { } });

        var result = await adapter.ExecuteAsync(CreateJob("bridge-endpoint-rejected", CadProvider.Tekla, payload), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(TeklaProviderAdapter.BridgeEndpointRejectedErrorCode, result.ErrorCode);
        Assert.Equal(0, tokenLoads);
        Assert.Null(handler.LastRequest);
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/capabilities")]
    [InlineData("/component/upsert")]
    [InlineData("/component/modify")]
    [InlineData("/component/delete")]
    [InlineData("/component/read")]
    [InlineData("/selection")]
    [InlineData("/model/selection")]
    [InlineData("/model/select")]
    [InlineData("/model/changes/apply")]
    [InlineData("/constructive/tekla-plan/prepare")]
    [InlineData("/constructive/tekla-plan/apply")]
    [InlineData("/pick/axis")]
    [InlineData("/uda/files/apply")]
    [InlineData("/expert-attributes/ifc-attribution/scan")]
    [InlineData("/expert-attributes/ifc-attribution/apply")]
    public async Task TeklaAdapter_ShouldForward_AllowedEndpoints(string endpoint)
    {
        var handler = new FakeBridgeHandler();
        var adapter = NewAdapterWithFakeBridge(handler);
        var payload = JsonElementFromAnonymous(new { endpoint, body = new { } });

        var result = await adapter.ExecuteAsync(CreateJob("bridge-endpoint-allowed", CadProvider.Tekla, payload), CancellationToken.None);

        Assert.True(result.IsSuccess, $"expected success, got {result.ErrorCode}");
    }

    [Fact]
    public async Task TeklaAdapter_ShouldReject_TokenPathOverride()
    {
        var handler = new FakeBridgeHandler();
        var tokenLoads = 0;
        var adapter = new TeklaProviderAdapter(
            tokenLoader: _ => { tokenLoads++; return "secret-token"; },
            httpClientFactory: _ => new HttpClient(handler));
        var payload = JsonElementFromAnonymous(new
        {
            endpoint = "/component/upsert",
            tokenPath = @"\\attacker\share\token.dat",
            body = new { x = 1 }
        });

        var result = await adapter.ExecuteAsync(CreateJob("token-path-rejected", CadProvider.Tekla, payload), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(TeklaProviderAdapter.TokenPathRejectedErrorCode, result.ErrorCode);
        Assert.Equal(0, tokenLoads);
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task TeklaAdapter_ShouldLoadToken_OnlyFromDefaultStore()
    {
        var handler = new FakeBridgeHandler();
        var requestedPaths = new List<string?>();
        var adapter = new TeklaProviderAdapter(
            tokenLoader: path => { requestedPaths.Add(path); return "tok"; },
            httpClientFactory: _ => new HttpClient(handler));
        var payload = JsonElementFromAnonymous(new { endpoint = "/component/read", body = new { } });

        var result = await adapter.ExecuteAsync(CreateJob("token-default-store", CadProvider.Tekla, payload), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Null(Assert.Single(requestedPaths));
    }

    // ────────── helpers ──────────

    private sealed class AutoCadSandbox
    {
        private bool _exeResolverCalled;

        public required string Root { get; init; }
        public required string StateRoot { get; init; }
        public required string PluginDirectory { get; init; }
        public required string PluginPath { get; init; }
        public required string ExePath { get; init; }
        public AutoCadProviderAdapter Adapter { get; private set; } = null!;
        public bool ExeResolverCalled => _exeResolverCalled;

        public static AutoCadSandbox Create(Func<string?>? exeResolver = null)
        {
            var root = Path.Combine(Path.GetTempPath(), "platform-autocad-a1", Guid.NewGuid().ToString("N"));
            var pluginDirectory = Path.Combine(root, "install", "plugins", "autocad");
            Directory.CreateDirectory(pluginDirectory);
            var pluginPath = Path.Combine(pluginDirectory, "Bridge.AutoCadPlugin.dll");
            File.WriteAllBytes(pluginPath, [0x4D, 0x5A]);
            // Локально «установленный AutoCAD» — безвредный системный exe, который сразу завершается.
            var exePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "whoami.exe");
            var sandbox = new AutoCadSandbox
            {
                Root = root,
                StateRoot = Path.Combine(root, "state"),
                PluginDirectory = pluginDirectory,
                PluginPath = pluginPath,
                ExePath = exePath,
            };
            var resolver = exeResolver ?? (() => exePath);
            sandbox.Adapter = new AutoCadProviderAdapter(
                sessionExecutorFactory: null,
                autoCadExeResolver: () => { sandbox._exeResolverCalled = true; return resolver(); },
                pluginDirectory: pluginDirectory,
                localStateRoot: sandbox.StateRoot);
            return sandbox;
        }
    }

    private static TeklaProviderAdapter NewAdapterWithFakeBridge(FakeBridgeHandler handler, string token = "fake-token")
        => new(
            tokenLoader: _ => token,
            httpClientFactory: _ => new HttpClient(handler));

    private static JsonElement JsonElementFromAnonymous(object src)
    {
        var json = JsonSerializer.Serialize(src);
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static ConnectorJobEnvelope CreateJob(string requestId, CadProvider provider, JsonElement payload)
    {
        return new ConnectorJobEnvelope(
            SchemaVersion: 1,
            RequestId: requestId,
            ModuleId: "bridge",
            Provider: provider,
            Operation: JobOperation.Build,
            CreatedAtUtc: DateTime.UtcNow,
            Payload: payload,
            CorrelationId: $"corr-{requestId}");
    }

    private sealed class FakeBridgeHandler : HttpMessageHandler
    {
        public HttpStatusCode HealthStatus { get; set; } = HttpStatusCode.OK;
        public HttpStatusCode ResponseStatus { get; set; } = HttpStatusCode.OK;
        public string ResponseBody { get; set; } = """{"ok":true,"echoed":"bridge"}""";

        public sealed record CapturedRequest(HttpMethod Method, string Path, string Token, string Body, string Host, int Port);
        public CapturedRequest? LastRequest { get; private set; }
        public int PostCallCount { get; private set; }

        private readonly Queue<(HttpStatusCode Status, string Body)> _queued = new();
        public void QueueResponse(HttpStatusCode status, string body) => _queued.Enqueue((status, body));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == "/health")
            {
                return new HttpResponseMessage(HealthStatus)
                {
                    Content = new StringContent("""{"ok":true,"teklaConnected":true}""", Encoding.UTF8, "application/json"),
                };
            }

            var token = request.Headers.TryGetValues("X-Bridge-Token", out var values)
                ? string.Join(",", values)
                : "";
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            LastRequest = new CapturedRequest(request.Method, path, token, body, request.RequestUri.Host, request.RequestUri.Port);
            PostCallCount++;

            var (status, responseBody) = _queued.Count > 0
                ? _queued.Dequeue()
                : (ResponseStatus, ResponseBody);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class FakeRestarter : IBridgeDesktopRestarter
    {
        public bool IsAvailable { get; set; } = true;
        public bool ReturnValue { get; set; } = true;
        public int CallCount { get; private set; }
        public Action<string>? OnRestart { get; set; }

        public Task<bool> RestartAndWaitAsync(
            string bridgeBaseUrl,
            Func<string, HttpClient> httpClientFactory,
            TimeSpan totalTimeout,
            CancellationToken cancellationToken)
        {
            CallCount++;
            OnRestart?.Invoke(bridgeBaseUrl);
            return Task.FromResult(ReturnValue);
        }
    }
}
