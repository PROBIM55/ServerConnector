using System.IO.Compression;
using System.Text;

namespace Connector.AgrConversion.Tests;

/// <summary>
/// Тексты ошибок для пользователя: по-русски, с именем архива или файла, без сырого текста исключения; сырой текст —
/// только в <see cref="AgrReadException.Diagnostic"/>. Плюс устаревшая строка ошибки assimp на пустом FBX.
/// </summary>
public class ErrorMessageTests
{
    static AgrReadException AssertUserMessage(Exception? ex, string file)
    {
        var e = Assert.IsType<AgrReadException>(ex);
        Assert.Equal(file, e.File);
        Assert.Contains(file, e.Message);
        Assert.Matches("[а-яё]", e.Message);
        Assert.NotEqual("", e.Diagnostic);
        Assert.DoesNotContain(e.Diagnostic, e.Message);
        if (e.InnerException != null)
        {
            Assert.DoesNotContain(e.InnerException.Message, e.Message);
            Assert.Contains(e.InnerException.Message, e.Diagnostic);
        }
        return e;
    }

    static string CopyFixture(TempDir tmp, string tag)
    {
        string dir = Path.Combine(tmp.Path, tag, TestPaths.FixturePart);
        TestPaths.CopyDirectory(TestPaths.FixtureDir, dir);
        return dir;
    }

    [Fact]
    public void BrokenZip_NamesArchive()
    {
        using var tmp = new TempDir("err-zip");
        string zip = Path.Combine(tmp.Path, "SM_Bad_001.zip");
        File.WriteAllBytes(zip, new byte[] { 0x50, 0x4B, 0x03, 0x04, 1, 2, 3, 4, 5, 6, 7, 8 });
        string work = Path.Combine(tmp.Path, "work");

        var ex = Record.Exception(() => new AgrPackageReader(work).ReadPart(AgrPackageReader.Discover(zip).Single()));

        var e = AssertUserMessage(ex, "SM_Bad_001.zip");
        Assert.IsType<InvalidDataException>(e.InnerException);
        Assert.Empty(Directory.EnumerateFileSystemEntries(work));
    }

    [Fact]
    public void ZipWithoutFbx_NamesArchive()
    {
        using var tmp = new TempDir("err-nofbx");
        string zip = Path.Combine(tmp.Path, "SM_NoFbx_001.zip");
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
        using (var w = new StreamWriter(z.CreateEntry("SM_NoFbx_001/readme.txt").Open())) w.Write("x");
        string work = Path.Combine(tmp.Path, "work");

        var ex = Record.Exception(() => new AgrPackageReader(work).ReadPart(AgrPackageReader.Discover(zip).Single()));

        var e = Assert.IsType<AgrReadException>(ex);
        Assert.Equal("SM_NoFbx_001.zip", e.File);
        Assert.Contains("SM_NoFbx_001.zip", e.Message);
        Assert.Contains("SM_*.fbx", e.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(work));
    }

    [Fact]
    public void BrokenPng_NamesFile_AndPartFails()
    {
        using var tmp = new TempDir("err-png");
        string part = CopyFixture(tmp, "png");
        const string png = "T_TestPart_001_Diffuse_1.1001.png";
        Assert.True(File.Exists(Path.Combine(part, png)));
        File.WriteAllBytes(Path.Combine(part, png), new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4, 5 });

        var ex = Record.Exception(() => new AgrPartReader().Read(part));

        AssertUserMessage(ex, png);
    }

    [Fact]
    public void LockedFbx_NamesFile_InRussian()
    {
        using var tmp = new TempDir("err-lock");
        string part = CopyFixture(tmp, "lock");
        const string fbx = "SM_TestPart_001.fbx";
        Exception? ex;
        using (new FileStream(Path.Combine(part, fbx), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            ex = Record.Exception(() => new AgrPartReader().Read(part));
        }

        var e = AssertUserMessage(ex, fbx);
        Assert.IsAssignableFrom<IOException>(e.InnerException);
        Assert.Contains("занят другим процессом", e.Message);
    }

    [Fact]
    public void EmptyFbx_AfterSuccessfulImport_HasNoStaleAssimpError()
    {
        // неудачный импорт кладёт текст в глобальную строку ошибки assimp, удачный её не очищает
        var bad = AssertUserMessage(Record.Exception(() => AssimpFbxReader.Read(Encoding.ASCII.GetBytes("garbage, not fbx"), "bad.fbx")), "bad.fbx");
        Assert.Contains("assimp", bad.Diagnostic);
        string stale = bad.Diagnostic[(bad.Diagnostic.IndexOf("байт: ", StringComparison.Ordinal) + 6)..];
        Assert.NotEqual("", stale);
        Assert.Equal(7, new AgrPartReader().Read(TestPaths.FixtureDir).ModelTriangles);

        using var tmp = new TempDir("err-empty");
        string part = CopyFixture(tmp, "empty");
        File.WriteAllBytes(Path.Combine(part, "SM_TestPart_001.fbx"), Array.Empty<byte>());
        var ex = Record.Exception(() => new AgrPartReader().Read(part));

        var e = Assert.IsType<AgrReadException>(ex);
        Assert.Contains("SM_TestPart_001.fbx", e.Message);
        Assert.Contains("0 байт", e.Message);
        Assert.DoesNotContain(stale, e.Message);
        Assert.DoesNotContain(stale, e.Diagnostic);
    }
}
