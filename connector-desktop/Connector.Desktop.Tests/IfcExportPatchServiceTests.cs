using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Connector.Desktop.Services;
using Xunit;

namespace Connector.Desktop.Tests;

public sealed class IfcExportPatchServiceTests
{
    [Fact]
    public void SecondDllWriteFailure_RestoresExactFreshInstallState()
    {
        using var fixture = new PatchFixture();
        fixture.PrepareApplySet("v1", includeXsd: false);
        var before = fixture.CaptureApplyFiles(includeXsd: false);
        var service = fixture.CreateService((point, path) =>
        {
            if (point == IfcPatchIoPoint.WritePatchedFile && Path.GetFileName(path) == "Model.dll")
                throw new IOException("injected second DLL write failure");
        });

        var result = service.Apply(fixture.Request);

        Assert.False(result.IsSuccess);
        Assert.Contains("полностью откатаны", result.Message);
        fixture.AssertApplyFilesEqual(before, includeXsd: false);
    }

    [Fact]
    public void ReapplyVerificationFailure_RestoresImmediatePreOperationPatchAndSidecar()
    {
        using var fixture = new PatchFixture();
        fixture.PrepareApplySet("v1", includeXsd: false);
        Assert.True(fixture.CreateService().Apply(fixture.Request).IsSuccess);
        var beforeReapply = fixture.CaptureApplyFiles(includeXsd: false);
        fixture.WriteStagedPatch("v2", includeXsd: false);
        var service = fixture.CreateService((point, path) =>
        {
            if (point == IfcPatchIoPoint.VerifyPatchedFile && Path.GetFileName(path) == "Model.dll")
                throw new IOException("injected verification failure");
        });

        var result = service.Apply(fixture.Request);

        Assert.False(result.IsSuccess);
        Assert.Contains("полностью откатаны", result.Message);
        fixture.AssertApplyFilesEqual(beforeReapply, includeXsd: false);
        Assert.Equal("patched-v1-IFCExport4.dll", File.ReadAllText(fixture.LivePath));
    }

    [Fact]
    public void XsdVerificationFailure_RestoresXsdDllsBackupsAndAbsentSidecar()
    {
        using var fixture = new PatchFixture();
        fixture.PrepareApplySet("v1", includeXsd: true);
        var before = fixture.CaptureApplyFiles(includeXsd: true);
        var service = fixture.CreateService((point, _) =>
        {
            if (point == IfcPatchIoPoint.VerifyXsd)
                throw new IOException("injected XSD verification failure");
        });

        var result = service.Apply(fixture.Request);

        Assert.False(result.IsSuccess);
        Assert.Contains("полностью откатаны", result.Message);
        fixture.AssertApplyFilesEqual(before, includeXsd: true);
    }

    [Fact]
    public void StateVerificationFailure_RestoresPreviousSidecarAtomically()
    {
        using var fixture = new PatchFixture();
        fixture.PrepareApplySet("v1", includeXsd: false);
        Assert.True(fixture.CreateService().Apply(fixture.Request).IsSuccess);
        var beforeReapply = fixture.CaptureApplyFiles(includeXsd: false);
        fixture.WriteStagedPatch("v2", includeXsd: false);
        var service = fixture.CreateService((point, _) =>
        {
            if (point == IfcPatchIoPoint.VerifyState)
                throw new IOException("injected sidecar verification failure");
        });

        var result = service.Apply(fixture.Request);

        Assert.False(result.IsSuccess);
        Assert.Contains("полностью откатаны", result.Message);
        fixture.AssertApplyFilesEqual(beforeReapply, includeXsd: false);
    }

    [Fact]
    public void ConcurrentLiveChange_IsNotOverwrittenAndReportsManualReview()
    {
        using var fixture = new PatchFixture();
        fixture.PrepareApplySet("v1", includeXsd: false);
        var service = fixture.CreateService((point, path) =>
        {
            if (point == IfcPatchIoPoint.VerifyPatchedFile && Path.GetFileName(path) == "IFCExport4.dll")
            {
                File.WriteAllText(path, "external-concurrent-write");
                throw new IOException("injected concurrent write");
            }
        });

        var result = service.Apply(fixture.Request);

        Assert.False(result.IsSuccess);
        Assert.Contains("ручная проверка", result.Message);
        Assert.Contains("изменён другим процессом", result.TechnicalDetails);
        Assert.Equal("external-concurrent-write", File.ReadAllText(fixture.LivePath));
        Assert.Equal("stock-Model.dll", File.ReadAllText(fixture.ModelLivePath));
    }

    [Fact]
    public void RollbackConcurrentChange_IsPreservedAndEarlierRestoreIsReversed()
    {
        using var fixture = new PatchFixture();
        fixture.PrepareApplySet("v1", includeXsd: false);
        Assert.True(fixture.CreateService().Apply(fixture.Request).IsSuccess);
        var originalSidecar = File.ReadAllBytes(fixture.StatePath);
        var service = fixture.CreateService((point, path) =>
        {
            if (point == IfcPatchIoPoint.RollbackFile && Path.GetFileName(path) == "Model.dll")
                File.WriteAllText(path, "external-during-rollback");
        });

        var result = service.Rollback(fixture.BinPath);

        Assert.False(result.IsSuccess);
        Assert.Contains("ручная проверка", result.Message);
        Assert.Equal("patched-v1-IFCExport4.dll", File.ReadAllText(fixture.LivePath));
        Assert.Equal("external-during-rollback", File.ReadAllText(fixture.ModelLivePath));
        Assert.Equal(originalSidecar, File.ReadAllBytes(fixture.StatePath));
    }

    [Fact]
    public void ReparseDirectoryInTargetPath_BlocksApplyBeforeOutsideWrite()
    {
        using var fixture = new PatchFixture();
        fixture.PrepareApplySet("v1", includeXsd: false);
        var outside = Path.Combine(fixture.Root, "outside-features");
        Directory.CreateDirectory(outside);
        var outsideLive = Path.Combine(outside, "PropertyPaneFeature.dll");
        File.WriteAllText(outsideLive, "outside-stock");
        var link = Path.Combine(fixture.BinPath, "Features");
        CreateDirectoryJunction(link, outside);
        try
        {
            fixture.WriteSingleTargetManifest("Features\\PropertyPaneFeature.dll", "outside-patched");

            var result = fixture.CreateService().Apply(fixture.Request);

            Assert.False(result.IsSuccess);
            Assert.Contains("небезопасный путь", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("outside-stock", File.ReadAllText(outsideLive));
            Assert.False(File.Exists(outsideLive + ".ifc-orig"));
            Assert.False(File.Exists(fixture.StatePath));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    private static void CreateDirectoryJunction(string link, string target)
    {
        var start = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("/d");
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("mklink");
        start.ArgumentList.Add("/J");
        start.ArgumentList.Add(link);
        start.ArgumentList.Add(target);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, "mklink /J failed: " + stdout + stderr);
        Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
    }

    [Fact]
    public void UntrackedStructuraXsdMarker_BlocksApplyBeforeDllMutation()
    {
        using var fixture = new PatchFixture();
        fixture.PrepareApplySet("v1", includeXsd: true);
        File.AppendAllText(fixture.XsdPath, "<!-- Structura-IFC-pset-entities -->");
        var dllBefore = File.ReadAllBytes(fixture.LivePath);

        var result = fixture.CreateService().Apply(fixture.Request);

        Assert.False(result.IsSuccess);
        Assert.Contains("маркер Structura", result.Message);
        Assert.Equal(dllBefore, File.ReadAllBytes(fixture.LivePath));
        Assert.False(File.Exists(fixture.BackupPath));
        Assert.False(File.Exists(fixture.XsdPath + ".bak"));
        Assert.False(File.Exists(fixture.StatePath));
    }

    [Fact]
    public void ReparseEnvironmentsDirectory_BlocksApplyBeforeExternalXsdReadOrWrite()
    {
        using var fixture = new PatchFixture();
        fixture.PrepareApplySet("v1", includeXsd: true);
        var environments = Path.Combine(fixture.Root, "Environments");
        Directory.Move(environments, environments + "-original");
        var outside = Path.Combine(fixture.Root, "outside-environments");
        var outsideXsd = Path.Combine(outside, "common", "inp", "IfcPropertySetConfigurations.xsd");
        Directory.CreateDirectory(Path.GetDirectoryName(outsideXsd)!);
        File.WriteAllText(outsideXsd, "external-xsd");
        CreateDirectoryJunction(environments, outside);
        try
        {
            var result = fixture.CreateService().Apply(fixture.Request);

            Assert.False(result.IsSuccess);
            Assert.Contains("Путь XSD", result.Message);
            Assert.Equal("external-xsd", File.ReadAllText(outsideXsd));
            Assert.False(File.Exists(outsideXsd + ".bak"));
            Assert.Equal("stock-IFCExport4.dll", File.ReadAllText(fixture.LivePath));
            Assert.False(File.Exists(fixture.BackupPath));
        }
        finally
        {
            Directory.Delete(environments);
        }
    }

    [Fact]
    public void ReparseEnvironmentsDirectory_BlocksRollbackWithoutTouchingExternalXsd()
    {
        using var fixture = new PatchFixture();
        fixture.PrepareApplySet("v1", includeXsd: true);
        Assert.True(fixture.CreateService().Apply(fixture.Request).IsSuccess);
        var environments = Path.Combine(fixture.Root, "Environments");
        Directory.Move(environments, environments + "-patched-original");
        var outside = Path.Combine(fixture.Root, "outside-rollback-environments");
        var outsideXsd = Path.Combine(outside, "common", "inp", "IfcPropertySetConfigurations.xsd");
        Directory.CreateDirectory(Path.GetDirectoryName(outsideXsd)!);
        File.WriteAllText(outsideXsd, "external-live-xsd");
        File.WriteAllText(outsideXsd + ".bak", "external-backup-xsd");
        CreateDirectoryJunction(environments, outside);
        try
        {
            var result = fixture.CreateService().Rollback(fixture.BinPath);

            Assert.False(result.IsSuccess);
            Assert.Contains("ручной проверки", result.Message);
            Assert.Equal("external-live-xsd", File.ReadAllText(outsideXsd));
            Assert.Equal("external-backup-xsd", File.ReadAllText(outsideXsd + ".bak"));
            Assert.Equal("patched-v1-IFCExport4.dll", File.ReadAllText(fixture.LivePath));
            Assert.True(File.Exists(fixture.StatePath));
        }
        finally
        {
            Directory.Delete(environments);
        }
    }

    [Fact]
    public void UnknownLiveDll_BlocksApplyAndRollbackWithoutChangingFiles()
    {
        using var fixture = new PatchFixture();
        fixture.WriteState("unexpected");
        var liveBefore = File.ReadAllBytes(fixture.LivePath);
        var backupBefore = File.ReadAllBytes(fixture.BackupPath);

        var status = fixture.Service.GetStatus(fixture.BinPath);
        Assert.True(status.NeedsManualReview);
        Assert.False(status.NeedsReapply);
        Assert.False(status.Applied);

        if (!fixture.Service.IsTeklaRunning())
        {
            var apply = fixture.Service.Apply(new IfcPatchRequest { TeklaBin = fixture.BinPath, StagingDir = fixture.Root });
            Assert.False(apply.IsSuccess);
            Assert.Contains("ручной проверки", apply.Message);
        }
        var rollback = fixture.Service.Rollback(fixture.BinPath);
        Assert.False(rollback.IsSuccess);
        Assert.Contains("ручной проверки", rollback.Message);
        Assert.Equal(liveBefore, File.ReadAllBytes(fixture.LivePath));
        Assert.Equal(backupBefore, File.ReadAllBytes(fixture.BackupPath));
        Assert.True(File.Exists(fixture.StatePath));
    }

    [Fact]
    public void CorruptBackup_BlocksRollbackAndDoesNotRewriteLiveDll()
    {
        using var fixture = new PatchFixture();
        fixture.WriteState("patched");
        File.WriteAllText(fixture.BackupPath, "corrupted-backup");
        var liveBefore = File.ReadAllBytes(fixture.LivePath);

        Assert.True(fixture.Service.GetStatus(fixture.BinPath).NeedsManualReview);
        var rollback = fixture.Service.Rollback(fixture.BinPath);
        Assert.False(rollback.IsSuccess);
        Assert.Equal(liveBefore, File.ReadAllBytes(fixture.LivePath));
    }

    [Fact]
    public void KnownPristineLiveFile_ReportsSafeReapplyAndRollsBack()
    {
        using var fixture = new PatchFixture();
        fixture.WriteState("pristine");

        var status = fixture.Service.GetStatus(fixture.BinPath);
        Assert.False(status.NeedsManualReview);
        Assert.True(status.NeedsReapply);
        Assert.False(status.Applied);

        var rollback = fixture.Service.Rollback(fixture.BinPath);
        Assert.True(rollback.IsSuccess, rollback.Message);
        Assert.Equal(Encoding.UTF8.GetBytes("pristine"), File.ReadAllBytes(fixture.LivePath));
        Assert.False(File.Exists(fixture.StatePath));
    }

    [Fact]
    public void UnreadableSidecar_DoesNotLookLikeFreshInstall()
    {
        using var fixture = new PatchFixture();
        fixture.WriteState("patched");
        File.WriteAllText(fixture.StatePath, "{invalid-json");
        var liveBefore = File.ReadAllBytes(fixture.LivePath);

        Assert.True(fixture.Service.GetStatus(fixture.BinPath).NeedsManualReview);
        if (!fixture.Service.IsTeklaRunning())
        {
            var apply = fixture.Service.Apply(new IfcPatchRequest { TeklaBin = fixture.BinPath, StagingDir = fixture.Root });
            Assert.False(apply.IsSuccess);
            Assert.Contains("Файл состояния", apply.Message);
        }
        Assert.Equal(liveBefore, File.ReadAllBytes(fixture.LivePath));
    }

    [Fact]
    public void SidecarPathTraversal_CannotRollbackOutsideTeklaBin()
    {
        using var fixture = new PatchFixture();
        fixture.WriteState("patched");
        var outside = Path.Combine(fixture.Root, "outside.dll");
        File.WriteAllText(outside, "patched");
        File.WriteAllText(outside + ".ifc-orig", "pristine");
        var state = new IfcPatchState
        {
            Files = new List<IfcPatchFileState>
            {
                new() { TargetRelpath = "..\\outside.dll", PristineSha = PatchFixture.Sha("pristine"),
                        PatchedSha = PatchFixture.Sha("patched") }
            }
        };
        File.WriteAllText(fixture.StatePath, JsonSerializer.Serialize(state));

        Assert.True(fixture.Service.GetStatus(fixture.BinPath).NeedsManualReview);
        Assert.False(fixture.Service.Rollback(fixture.BinPath).IsSuccess);
        Assert.Equal("patched", File.ReadAllText(outside));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChangedOrRedirectedXsd_CannotBeOverwrittenByRollback(bool redirect)
    {
        using var fixture = new PatchFixture();
        var state = fixture.WriteState("patched");
        var xsd = Path.GetFullPath(fixture.Service.ResolveXsdPath(fixture.BinPath));
        Directory.CreateDirectory(Path.GetDirectoryName(xsd)!);
        File.WriteAllText(xsd + ".bak", "pristine-xsd");
        File.WriteAllText(xsd, redirect ? "patched-xsd" : "external-change");
        state.XsdApplied = true;
        state.XsdPath = redirect ? Path.Combine(fixture.Root, "outside.xsd") : xsd;
        state.XsdPristineSha = PatchFixture.Sha("pristine-xsd");
        state.XsdPatchedSha = PatchFixture.Sha("patched-xsd");
        File.WriteAllText(fixture.StatePath, JsonSerializer.Serialize(state));
        if (redirect) File.WriteAllText(state.XsdPath, "foreign-xsd");
        var liveBefore = File.ReadAllText(xsd);

        Assert.True(fixture.Service.GetStatus(fixture.BinPath).NeedsManualReview);
        Assert.False(fixture.Service.Rollback(fixture.BinPath).IsSuccess);
        Assert.Equal(liveBefore, File.ReadAllText(xsd));
        if (redirect) Assert.Equal("foreign-xsd", File.ReadAllText(state.XsdPath));
    }

    private sealed class PatchFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "connector-ifc-patch-test-" + Guid.NewGuid().ToString("N"));
        public string BinPath => Path.Combine(Root, "bin");
        public string LivePath => Path.Combine(BinPath, "IFCExport4.dll");
        public string BackupPath => LivePath + ".ifc-orig";
        public string ModelLivePath => Path.Combine(BinPath, "Model.dll");
        public string StatePath => Path.Combine(BinPath, ".structura-ifc.json");
        public string StagingPath => Path.Combine(Root, "staging");
        public string XsdPath => Service.ResolveXsdPath(BinPath);
        public IfcPatchRequest Request => new() { TeklaBin = BinPath, StagingDir = StagingPath };
        public IfcExportPatchService Service { get; }

        public PatchFixture()
        {
            Directory.CreateDirectory(BinPath);
            Service = new IfcExportPatchService(Path.Combine(Root, "logs"));
        }

        public IfcExportPatchService CreateService(Action<IfcPatchIoPoint, string>? fault = null) =>
            new(Path.Combine(Root, "logs"), fault, () => false);

        public void PrepareApplySet(string version, bool includeXsd)
        {
            File.Copy(typeof(PatchFixture).Assembly.Location, Path.Combine(BinPath, "TeklaStructures.exe"));
            File.WriteAllText(LivePath, "stock-IFCExport4.dll");
            File.WriteAllText(ModelLivePath, "stock-Model.dll");
            if (includeXsd)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(XsdPath)!);
                File.WriteAllText(XsdPath,
                    "<xs:schema>\n  <xs:enumeration value=\"IfcWindow\">\n  </xs:enumeration>\n</xs:schema>\n");
            }
            WriteStagedPatch(version, includeXsd);
        }

        public void WriteStagedPatch(string version, bool includeXsd)
        {
            Directory.CreateDirectory(StagingPath);
            var entries = new List<IfcPatchFileEntry>();
            foreach (var name in new[] { "IFCExport4.dll", "Model.dll" })
            {
                var content = "patched-" + version + "-" + name;
                File.WriteAllText(Path.Combine(StagingPath, name), content);
                entries.Add(new IfcPatchFileEntry { TargetRelpath = name, Sha256 = Sha(content) });
            }
            var build = FileVersionInfo.GetVersionInfo(Path.Combine(BinPath, "TeklaStructures.exe")).FileVersion!;
            var manifest = new IfcPatchManifest
            {
                TeklaBuild = build,
                SetVersion = version,
                Files = entries,
                Xsd = includeXsd
                    ? new IfcXsdSpec { Entities = new Dictionary<string, string> { ["IfcCustomEntity"] = "ARCH" } }
                    : null
            };
            File.WriteAllText(Path.Combine(StagingPath, "manifest.json"), JsonSerializer.Serialize(manifest));
        }

        public void WriteSingleTargetManifest(string relativePath, string patchedContent)
        {
            var staged = Path.Combine(StagingPath, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
            File.WriteAllText(staged, patchedContent);
            var build = FileVersionInfo.GetVersionInfo(Path.Combine(BinPath, "TeklaStructures.exe")).FileVersion!;
            var manifest = new IfcPatchManifest
            {
                TeklaBuild = build,
                SetVersion = "reparse-test",
                Files = new List<IfcPatchFileEntry>
                {
                    new() { TargetRelpath = relativePath, Sha256 = Sha(patchedContent) }
                }
            };
            File.WriteAllText(Path.Combine(StagingPath, "manifest.json"), JsonSerializer.Serialize(manifest));
        }

        public Dictionary<string, byte[]?> CaptureApplyFiles(bool includeXsd)
        {
            var paths = new List<string>
            {
                LivePath, BackupPath, ModelLivePath, ModelLivePath + ".ifc-orig", StatePath
            };
            if (includeXsd) { paths.Add(XsdPath); paths.Add(XsdPath + ".bak"); }
            return paths.ToDictionary(path => path, path => File.Exists(path) ? File.ReadAllBytes(path) : null,
                StringComparer.OrdinalIgnoreCase);
        }

        public void AssertApplyFilesEqual(Dictionary<string, byte[]?> expected, bool includeXsd)
        {
            var actual = CaptureApplyFiles(includeXsd);
            foreach (var (path, bytes) in expected)
            {
                if (bytes is null) Assert.False(File.Exists(path), "Expected path to remain absent: " + path);
                else Assert.Equal(bytes, actual[path]);
            }
        }

        public IfcPatchState WriteState(string liveContent)
        {
            File.WriteAllText(LivePath, liveContent);
            File.WriteAllText(BackupPath, "pristine");
            var state = new IfcPatchState
            {
                TeklaBuild = "test",
                SetVersion = "test",
                Files = new List<IfcPatchFileState>
                {
                    new()
                    {
                        TargetRelpath = "IFCExport4.dll",
                        PristineSha = Sha("pristine"),
                        PatchedSha = Sha("patched")
                    }
                }
            };
            File.WriteAllText(StatePath, JsonSerializer.Serialize(state));
            return state;
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);

        public static string Sha(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
}
