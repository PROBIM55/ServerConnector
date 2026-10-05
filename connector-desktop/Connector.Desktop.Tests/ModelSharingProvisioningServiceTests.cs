using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Connector.Desktop.Services;
using Xunit;

namespace Connector.Desktop.Tests;

public sealed class ModelSharingProvisioningServiceTests
{
    [Fact]
    public void LegacyBackupWithoutSidecar_BlocksReprovisionAndPreservesDll()
    {
        using var fixture = new Fixture();
        fixture.WriteDll("legacy-patched", "pristine");
        File.WriteAllText(fixture.ConfigPath, "legacy-config");
        var original = File.ReadAllBytes(fixture.LivePath);
        var backup = File.ReadAllBytes(fixture.BackupPath);

        var status = fixture.Service.GetStatus(fixture.BinPath);
        Assert.True(status.NeedsManualReview);
        Assert.False(status.Provisioned);
        if (!fixture.Service.IsTeklaRunning())
        {
            var result = fixture.Service.Provision(fixture.Request());
            Assert.False(result.IsSuccess);
            Assert.Contains("ручной проверки", result.Message);
        }
        Assert.Equal(original, File.ReadAllBytes(fixture.LivePath));
        Assert.Equal(backup, File.ReadAllBytes(fixture.BackupPath));
        Assert.False(File.Exists(fixture.StatePath));
    }

    [Theory]
    [InlineData("external-dll", "managed-config")]
    [InlineData("patched", "external-config")]
    public void ChangedDllOrConfig_BlocksReprovision(string liveContent, string configContent)
    {
        using var fixture = new Fixture();
        fixture.WriteDll(liveContent, "pristine");
        File.WriteAllText(fixture.ConfigPath, configContent);
        fixture.WriteState();
        var dllBefore = File.ReadAllBytes(fixture.LivePath);
        var configBefore = File.ReadAllBytes(fixture.ConfigPath);

        Assert.True(fixture.Service.GetStatus(fixture.BinPath).NeedsManualReview);
        if (!fixture.Service.IsTeklaRunning())
        {
            var result = fixture.Service.Provision(fixture.Request());
            Assert.False(result.IsSuccess);
            Assert.Contains("ручной проверки", result.Message);
        }
        Assert.Equal(dllBefore, File.ReadAllBytes(fixture.LivePath));
        Assert.Equal(configBefore, File.ReadAllBytes(fixture.ConfigPath));
    }

    [Fact]
    public void CorruptSidecar_BlocksReprovision()
    {
        using var fixture = new Fixture();
        fixture.WriteDll("patched", "pristine");
        File.WriteAllText(fixture.StatePath, "{invalid-json");
        Assert.True(fixture.Service.GetStatus(fixture.BinPath).NeedsManualReview);
        if (!fixture.Service.IsTeklaRunning())
            Assert.False(fixture.Service.Provision(fixture.Request()).IsSuccess);
        Assert.Equal("patched", File.ReadAllText(fixture.LivePath));
    }

    [Fact]
    public void LegacySidecarWithoutConfigHash_AcceptsExactPreviouslyWrittenConfig()
    {
        using var fixture = new Fixture();
        fixture.WriteDll("patched", "pristine");
        File.WriteAllText(fixture.ConfigPath, LegacyConfig("sharing.internal", 9990), new UTF8Encoding(false));
        fixture.WriteState(configSha: "", serverHost: "sharing.internal", serverPort: 9990);

        var status = fixture.Service.GetStatus(fixture.BinPath);

        Assert.True(status.Provisioned);
        Assert.False(status.NeedsManualReview);
        Assert.False(status.NeedsReapply);
    }

    [Fact]
    public void MissingConfigWithExistingState_BlocksProvisionBeforeDllWrite()
    {
        using var fixture = new Fixture();
        fixture.WriteDll("patched", "pristine");
        fixture.WriteState();
        var dllBefore = File.ReadAllBytes(fixture.LivePath);
        var backupBefore = File.ReadAllBytes(fixture.BackupPath);

        Assert.True(fixture.Service.GetStatus(fixture.BinPath).NeedsManualReview);
        if (!fixture.Service.IsTeklaRunning())
        {
            var result = fixture.Service.Provision(fixture.Request());
            Assert.False(result.IsSuccess);
            Assert.Contains("ручной проверки", result.Message);
        }
        Assert.Equal(dllBefore, File.ReadAllBytes(fixture.LivePath));
        Assert.Equal(backupBefore, File.ReadAllBytes(fixture.BackupPath));
        Assert.False(File.Exists(fixture.ConfigPath));
    }

    [Fact]
    public void FirstProvisionFailureAfterConfig_RestoresAllPreviousFiles()
    {
        using var fixture = new Fixture(_ => Encoding.UTF8.GetBytes("new-patch"),
            stage => { if (stage == ModelSharingProvisionStage.AfterConfig) throw new IOException("injected-stop"); });
        File.WriteAllText(fixture.LivePath, "stock-dll");
        if (fixture.Service.IsTeklaRunning()) return;

        var result = fixture.Service.Provision(fixture.Request());

        Assert.False(result.IsSuccess);
        Assert.Contains("восстановлено", result.Message);
        Assert.Equal("stock-dll", File.ReadAllText(fixture.LivePath));
        Assert.False(File.Exists(fixture.BackupPath));
        Assert.False(File.Exists(fixture.ConfigPath));
        Assert.False(File.Exists(fixture.StatePath));
    }

    [Fact]
    public void ReprovisionFailureAfterDll_RestoresPreviouslyPatchedDllNotPristineBackup()
    {
        using var fixture = new Fixture(_ => Encoding.UTF8.GetBytes("new-patch"),
            stage => { if (stage == ModelSharingProvisionStage.AfterDll) throw new IOException("injected-stop"); });
        fixture.WriteDll("patched", "pristine");
        File.WriteAllText(fixture.ConfigPath, "managed-config");
        fixture.WriteState();
        var stateBefore = File.ReadAllBytes(fixture.StatePath);
        if (fixture.Service.IsTeklaRunning()) return;

        var result = fixture.Service.Provision(fixture.Request());

        Assert.False(result.IsSuccess);
        Assert.Equal("patched", File.ReadAllText(fixture.LivePath));
        Assert.Equal("pristine", File.ReadAllText(fixture.BackupPath));
        Assert.Equal("managed-config", File.ReadAllText(fixture.ConfigPath));
        Assert.Equal(stateBefore, File.ReadAllBytes(fixture.StatePath));
    }

    [Fact]
    public void StateWriteFailure_DoesNotReportSuccessOrLeavePatchApplied()
    {
        using var fixture = new Fixture(_ => Encoding.UTF8.GetBytes("new-patch"));
        File.WriteAllText(fixture.LivePath, "stock-dll");
        Directory.CreateDirectory(fixture.StatePath);
        if (fixture.Service.IsTeklaRunning()) return;

        var result = fixture.Service.Provision(fixture.Request());

        Assert.False(result.IsSuccess);
        Assert.Equal("stock-dll", File.ReadAllText(fixture.LivePath));
        Assert.False(File.Exists(fixture.BackupPath));
        Assert.False(File.Exists(fixture.ConfigPath));
    }

    [Fact]
    public void DirectoryAtStatePath_BlocksProvisionAndRetryBeforeAnyWrite()
    {
        using var fixture = new Fixture(_ => Encoding.UTF8.GetBytes("new-patch"));
        File.WriteAllText(fixture.LivePath, "stock-dll");
        Directory.CreateDirectory(fixture.StatePath);
        if (fixture.Service.IsTeklaRunning()) return;

        Assert.True(fixture.Service.GetStatus(fixture.BinPath).NeedsManualReview);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var result = fixture.Service.Provision(fixture.Request());
            Assert.False(result.IsSuccess);
            Assert.Contains("ручной проверки", result.Message);
            Assert.Equal("stock-dll", File.ReadAllText(fixture.LivePath));
            Assert.False(File.Exists(fixture.BackupPath));
            Assert.False(File.Exists(fixture.ConfigPath));
        }
    }

    [Fact]
    public void ConcurrentDllChangeDuringPatch_IsNotOverwritten()
    {
        Fixture? fixture = null;
        fixture = new Fixture(_ =>
        {
            File.WriteAllText(fixture!.LivePath, "external-change");
            return Encoding.UTF8.GetBytes("new-patch");
        });
        using (fixture)
        {
            File.WriteAllText(fixture.LivePath, "stock-dll");
            if (fixture.Service.IsTeklaRunning()) return;

            var result = fixture.Service.Provision(fixture.Request());

            Assert.False(result.IsSuccess);
            Assert.Contains("ручной проверки", result.Message);
            Assert.Equal("external-change", File.ReadAllText(fixture.LivePath));
            Assert.False(File.Exists(fixture.ConfigPath));
        }
    }

    [Fact]
    public void ConcurrentConfigChangeAfterDll_IsNotOverwritten()
    {
        Fixture? fixture = null;
        fixture = new Fixture(_ => Encoding.UTF8.GetBytes("new-patch"), stage =>
        {
            if (stage == ModelSharingProvisionStage.AfterDll)
                File.WriteAllText(fixture!.ConfigPath, "external-config");
        });
        using (fixture)
        {
            File.WriteAllText(fixture.LivePath, "stock-dll");
            if (fixture.Service.IsTeklaRunning()) return;

            var result = fixture.Service.Provision(fixture.Request());

            Assert.False(result.IsSuccess);
            Assert.Contains("ручной проверки", result.Message);
            Assert.Equal("stock-dll", File.ReadAllText(fixture.LivePath));
            Assert.Equal("external-config", File.ReadAllText(fixture.ConfigPath));
        }
    }

    [Fact]
    public void ExclusiveCommitHandle_BlocksForeignWritesAfterByteCheck()
    {
        Fixture? fixture = null;
        var blocked = new HashSet<ModelSharingWriteStage>();
        fixture = new Fixture(_ => Encoding.UTF8.GetBytes("new-patch"), writeHook: stage =>
        {
            var path = stage switch
            {
                ModelSharingWriteStage.BeforeDllCommit => fixture!.LivePath,
                ModelSharingWriteStage.BeforeConfigCommit => fixture!.ConfigPath,
                _ => fixture!.StatePath
            };
            try { File.WriteAllText(path, "external-change"); }
            catch (IOException) { blocked.Add(stage); }
        });
        using (fixture)
        {
            fixture.WriteDll("patched", "pristine");
            File.WriteAllText(fixture.ConfigPath, "managed-config");
            fixture.WriteState();
            if (fixture.Service.IsTeklaRunning()) return;

            var result = fixture.Service.Provision(fixture.Request());

            Assert.True(result.IsSuccess, result.Message + " " + result.TechnicalDetails);
            Assert.Equal(3, blocked.Count);
            Assert.Equal("new-patch", File.ReadAllText(fixture.LivePath));
            Assert.DoesNotContain("external-change", File.ReadAllText(fixture.ConfigPath));
            Assert.DoesNotContain("external-change", File.ReadAllText(fixture.StatePath));
        }
    }

    [Fact]
    public void ConcurrentExternalDllChange_IsPreservedAndReportedAsManualReview()
    {
        Fixture? fixture = null;
        fixture = new Fixture(_ => Encoding.UTF8.GetBytes("new-patch"), stage =>
        {
            if (stage != ModelSharingProvisionStage.AfterConfig) return;
            File.WriteAllText(fixture!.LivePath, "external-change");
            throw new IOException("injected-stop");
        });
        using (fixture)
        {
            File.WriteAllText(fixture.LivePath, "stock-dll");
            if (fixture.Service.IsTeklaRunning()) return;

            var result = fixture.Service.Provision(fixture.Request());

            Assert.False(result.IsSuccess);
            Assert.Contains("ручной проверки", result.Message);
            Assert.Equal("external-change", File.ReadAllText(fixture.LivePath));
            Assert.True(File.Exists(fixture.BackupPath));
            Assert.False(File.Exists(fixture.ConfigPath));
        }
    }

    private static string LegacyConfig(string host, int port) =>
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
        "<SharingConfiguration>\r\n" +
        "    <Parameter>\r\n" +
        "        <ServiceType>OnPremises</ServiceType>\r\n" +
        "        <ServerName>" + host + "</ServerName>\r\n" +
        "        <ServerPort>" + port + "</ServerPort>\r\n" +
        "    </Parameter>\r\n" +
        "</SharingConfiguration>\r\n";

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "connector-ms-test-" + Guid.NewGuid().ToString("N"));
        public string BinPath => Path.Combine(Root, "bin");
        public string LivePath => Path.Combine(BinPath, "Features", "SharingUIFeature.dll");
        public string BackupPath => LivePath + ".trimble-orig";
        public string StatePath => LivePath + ".structura-ms.json";
        public string ConfigPath => Path.Combine(BinPath, "SharingConfiguration.xml");
        public ModelSharingProvisioningService Service { get; }

        public Fixture(Func<byte[], byte[]>? patcher = null, Action<ModelSharingProvisionStage>? stageHook = null,
            Action<ModelSharingWriteStage>? writeHook = null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LivePath)!);
            Service = new ModelSharingProvisioningService(Path.Combine(Root, "logs"), patcher, stageHook, writeHook);
        }

        public void WriteDll(string live, string pristine)
        {
            File.WriteAllText(LivePath, live);
            File.WriteAllText(BackupPath, pristine);
        }

        public void WriteState(string? configSha = null, string serverHost = "localhost", int serverPort = 9990)
        {
            var state = new ModelSharingState
            {
                PristineSha = Sha("pristine"),
                PatchedSha = Sha("patched"),
                ConfigSha = configSha ?? Sha("managed-config"),
                ServerHost = serverHost,
                ServerPort = serverPort
            };
            File.WriteAllText(StatePath, JsonSerializer.Serialize(state));
        }

        public ModelSharingProvisionRequest Request() => new()
        {
            TeklaBin = BinPath, IdentityEmail = "test@example.invalid", IdentityName = "Test",
            ServerHost = "localhost", ServerPort = 9990
        };

        public void Dispose() => Directory.Delete(Root, recursive: true);

        private static string Sha(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
}
