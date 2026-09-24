using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Connector.AgrConversion.Tests;

public static class TestPaths
{
    public const string FixturePart = "SM_TestPart_001";

    /// <summary>Папка с эталонами АГР (tmp/agr_poc): batch/src, batch/ref, tiles_src. Без неё эталонные тесты пропускаются.</summary>
    public const string RefRootVariable = "AGR_REF_ROOT";

    public static string FixtureDir => Path.Combine(AppContext.BaseDirectory, "Fixtures", FixturePart);

    public static string? RefRoot
    {
        get
        {
            string? v = Environment.GetEnvironmentVariable(RefRootVariable);
            return !string.IsNullOrWhiteSpace(v) && Directory.Exists(v) ? v : null;
        }
    }

    public static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.EnumerateFiles(from))
        {
            File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
        }
        foreach (var d in Directory.EnumerateDirectories(from))
        {
            CopyDirectory(d, Path.Combine(to, Path.GetFileName(d)));
        }
    }

    /// <summary>zip части: папка части внутри архива (как в пакете АГР).</summary>
    public static string ZipPart(string partDir, string zipPath)
    {
        ZipFile.CreateFromDirectory(partDir, zipPath, CompressionLevel.Fastest, includeBaseDirectory: true);
        return zipPath;
    }
}

/// <summary>Временная папка теста под TEMP (на G: при запуске через env.sh пакета).</summary>
public sealed class TempDir : IDisposable
{
    public TempDir(string tag)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "agr-c1a-tests", tag + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

public sealed class RefFactAttribute : FactAttribute
{
    public RefFactAttribute()
    {
        if (TestPaths.RefRoot == null)
        {
            Skip = $"эталонный прогон: задайте {TestPaths.RefRootVariable} (папка tmp/agr_poc)";
        }
    }
}

public sealed class RefTheoryAttribute : TheoryAttribute
{
    public RefTheoryAttribute()
    {
        if (TestPaths.RefRoot == null)
        {
            Skip = $"эталонный прогон: задайте {TestPaths.RefRootVariable} (папка tmp/agr_poc)";
        }
    }
}

/// <summary>Запись PNG без WIC (zlib + CRC32): точные значения пикселей, 8 или 16 бит, серый/RGB/RGBA.</summary>
public static class PngTestWriter
{
    static readonly uint[] CrcTable = MakeTable();

    public static void Write(string path, int w, int h, int channels, int bitDepth, Func<int, int, int[]> pixel)
    {
        byte colorType = channels switch
        {
            1 => 0,
            3 => 2,
            4 => 6,
            _ => throw new ArgumentOutOfRangeException(nameof(channels)),
        };
        var raw = new MemoryStream();
        for (int y = 0; y < h; y++)
        {
            raw.WriteByte(0);
            for (int x = 0; x < w; x++)
            {
                int[] px = pixel(x, y);
                for (int c = 0; c < channels; c++)
                {
                    if (bitDepth == 16)
                    {
                        raw.WriteByte((byte)(px[c] >> 8));
                        raw.WriteByte((byte)(px[c] & 0xFF));
                    }
                    else
                    {
                        raw.WriteByte((byte)px[c]);
                    }
                }
            }
        }
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0), w);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
        ihdr[8] = (byte)bitDepth;
        ihdr[9] = colorType;
        var z = new MemoryStream();
        using (var zs = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true))
        {
            raw.Position = 0;
            raw.CopyTo(zs);
        }
        using var fs = File.Create(path);
        fs.Write(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A });
        Chunk(fs, "IHDR", ihdr);
        Chunk(fs, "IDAT", z.ToArray());
        Chunk(fs, "IEND", Array.Empty<byte>());
    }

    static void Chunk(Stream s, string type, byte[] data)
    {
        var buf = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buf, data.Length);
        s.Write(buf);
        byte[] t = Encoding.ASCII.GetBytes(type);
        s.Write(t);
        s.Write(data);
        uint c = 0xFFFFFFFFu;
        foreach (byte b in t) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        foreach (byte b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        BinaryPrimitives.WriteUInt32BigEndian(buf, c ^ 0xFFFFFFFFu);
        s.Write(buf);
    }

    static uint[] MakeTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }
}
