using System.IO.Compression;
using System.Text;

namespace Connector.AgrConversion.Tests;

/// <summary>Пакет: папка/zip части, папка пакета, набор zip; распаковка под рабочим корнем и её удаление (C1 (б)).</summary>
public class PackageReaderTests
{
    static string Unzipped(string work) =>
        Directory.Exists(work) ? string.Join(", ", Directory.EnumerateFileSystemEntries(work)) : "";

    [Fact]
    public void Zip_IsReadLikeFolder_AndExtractionIsDeleted()
    {
        using var tmp = new TempDir("zip");
        string zip = TestPaths.ZipPart(TestPaths.FixtureDir, Path.Combine(tmp.Path, "SM_TestPart_001.zip"));
        string work = Path.Combine(tmp.Path, "work");
        var src = Assert.Single(AgrPackageReader.Discover(zip));
        Assert.Equal(AgrSourceKind.Zip, src.Kind);
        Assert.Equal("SM_TestPart_001", src.PartName);

        var part = new AgrPackageReader(work).ReadPart(src);
        Assert.Equal("SM_TestPart_001", part.Name);
        Assert.Equal(7, part.ModelTriangles);
        Assert.Equal(4, part.Tiles.Count);
        Assert.Equal("zip SM_TestPart_001.zip", part.Source);
        Assert.Equal("", Unzipped(work));
    }

    [Fact]
    public void Extraction_IsDeleted_WhenReadFails()
    {
        using var tmp = new TempDir("bad");
        string partDir = Path.Combine(tmp.Path, "src", "SM_TestPart_001");
        TestPaths.CopyDirectory(TestPaths.FixtureDir, partDir);
        File.WriteAllBytes(Path.Combine(partDir, "SM_TestPart_001.fbx"), Encoding.ASCII.GetBytes("Kaydara FBX Binary  broken"));
        string zip = TestPaths.ZipPart(partDir, Path.Combine(tmp.Path, "SM_TestPart_001.zip"));
        string work = Path.Combine(tmp.Path, "work");
        var reader = new AgrPackageReader(work);

        var ex = Record.Exception(() => reader.ReadPart(AgrPackageReader.Discover(zip).Single()));
        Assert.NotNull(ex);
        Assert.True(Directory.Exists(work));
        Assert.Equal("", Unzipped(work));
    }

    [Fact]
    public void ZipSlip_IsRejected_AndNothingIsLeft()
    {
        using var tmp = new TempDir("slip");
        string zip = Path.Combine(tmp.Path, "SM_Evil_001.zip");
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(z.CreateEntry("SM_Evil_001/SM_Evil_001.fbx").Open())) w.Write("x");
            using (var w = new StreamWriter(z.CreateEntry("../evil.txt").Open())) w.Write("x");
        }
        string work = Path.Combine(tmp.Path, "work");
        // Без защиты путей распаковка тоже падает (FBX «x» не читается), поэтому тип исключения порчу не ловит.
        var ex = Assert.Throws<AgrReadException>(() => new AgrPackageReader(work).ReadPart(AgrPackageReader.Discover(zip).Single()));
        // Распаковка идёт в work\agr-unzip-*\, так что ../evil.txt оказался бы в work\ — проверка именно там и первой.
        Assert.False(File.Exists(Path.Combine(work, "evil.txt")), "запись ../evil.txt вышла из папки распаковки в work");
        Assert.Equal("", Unzipped(work));
        Assert.Contains("../evil.txt", ex.Message);
        Assert.Contains("SM_Evil_001.zip", ex.Message);
    }

    [Fact]
    public void Discover_FindsPartFolderZipsAndFolders_LightIsNotMain()
    {
        using var tmp = new TempDir("disc");
        var own = Assert.Single(AgrPackageReader.Discover(TestPaths.FixtureDir));
        Assert.Equal(AgrSourceKind.Directory, own.Kind);
        Assert.Equal("SM_TestPart_001", own.PartName);

        string package = Path.Combine(tmp.Path, "package");
        Directory.CreateDirectory(package);
        string zip1 = TestPaths.ZipPart(TestPaths.FixtureDir, Path.Combine(package, "SM_TestPart_001.zip"));
        string second = Path.Combine(package, "SM_TestPart_002");
        TestPaths.CopyDirectory(TestPaths.FixtureDir, second);
        File.Move(Path.Combine(second, "SM_TestPart_001.fbx"), Path.Combine(second, "SM_TestPart_002.fbx"));
        File.Move(Path.Combine(second, "SM_TestPart_001_Light.fbx"), Path.Combine(second, "SM_TestPart_002_Light.fbx"));
        Directory.CreateDirectory(Path.Combine(package, "docs"));

        var all = AgrPackageReader.Discover(package);
        Assert.Equal(new[] { "SM_TestPart_001", "SM_TestPart_002" }, all.Select(s => s.PartName));
        Assert.Equal(new[] { AgrSourceKind.Zip, AgrSourceKind.Directory }, all.Select(s => s.Kind));

        string zip2 = TestPaths.ZipPart(second, Path.Combine(tmp.Path, "SM_TestPart_002.zip"));
        var set = AgrPackageReader.Discover(zip2, zip1);
        Assert.Equal(new[] { "SM_TestPart_001", "SM_TestPart_002" }, set.Select(s => s.PartName));

        Assert.Throws<InvalidDataException>(() => AgrPackageReader.Discover(zip1, TestPaths.FixtureDir));
    }
}
