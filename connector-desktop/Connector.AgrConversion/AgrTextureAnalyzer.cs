using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Connector.AgrConversion;

/// <summary>
/// Статистика текстур через WIC (System.Windows.Media.Imaging) по правилам B1: однотонность — разброс каждого
/// канала RGB не больше 2/255; средний цвет; альфа; R-канал ERM. 16-битные PNG (Rgb48/Rgba64/Gray16) читаются в
/// родном формате как v/65535: разброс и средние считаются по 16-битным значениям, без округления до 8 бит. Проверено
/// тестом: перевод WIC Rgb48 → Bgra32 гаммы не добавляет ((32768; 16384; 65535) → (128; 64; 255)).
/// <para>
/// Потокобезопасность: у экземпляра нет изменяемого состояния — буфер пикселей берётся на каждый вызов из
/// <see cref="ArrayPool{T}.Shared"/> и возвращается в finally. <see cref="Analyze"/> и <see cref="Fill"/> можно
/// вызывать на одном экземпляре из нескольких потоков. Не потокобезопасен результат: один
/// <see cref="AgrTextureInfo"/> из нескольких потоков заполнять нельзя.
/// </para>
/// Картинка, которую WIC не прочитал, роняет чтение части (<see cref="AgrReadException"/> с именем файла): молча
/// терять текстуру нельзя.
/// </summary>
public sealed class AgrTextureAnalyzer
{
    static readonly Regex NameRx = new(
        @"^T_(?<stem>.+?)_(?<kind>[A-Za-z]+)_(?<set>\d+)\.(?<tile>\d{4})\.(?<ext>png|jpe?g|tiff?)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    static readonly Dictionary<string, string> KindMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["diffuse"] = "Diffuse", ["basecolor"] = "Diffuse", ["albedo"] = "Diffuse", ["color"] = "Diffuse",
        ["erm"] = "ERM", ["normal"] = "Normal", ["nrm"] = "Normal",
    };

    public sealed record TextureName(string Stem, string Kind, string KindRaw, string Set, int Tile);

    /// <summary>Разбор имени T_&lt;stem&gt;_&lt;Kind&gt;_&lt;set&gt;.&lt;UDIM&gt;.png; null — не текстура АГР.</summary>
    public static TextureName? ParseName(string fileName)
    {
        var m = NameRx.Match(fileName);
        if (!m.Success)
        {
            return null;
        }
        string raw = m.Groups["kind"].Value;
        string kind = KindMap.TryGetValue(raw, out var k) ? k : raw;
        return new TextureName(m.Groups["stem"].Value, kind, raw, m.Groups["set"].Value, int.Parse(m.Groups["tile"].Value));
    }

    public AgrTextureInfo Analyze(string path, string relativePath)
    {
        string file = Path.GetFileName(path);
        var name = ParseName(file) ?? throw new ArgumentException($"{file}: имя не по шаблону T_*_<вид>_<набор>.<UDIM>.png");
        var info = new AgrTextureInfo
        {
            File = file,
            RelativePath = relativePath,
            Stem = name.Stem,
            Kind = name.Kind,
            KindRaw = name.KindRaw,
            Set = name.Set,
            Tile = name.Tile,
            Bytes = new FileInfo(path).Length,
        };
        Fill(path, info);
        return info;
    }

    /// <summary>
    /// Заполняет статистику пикселей в <paramref name="info"/>. Нечитаемая картинка — <see cref="AgrReadException"/>
    /// с именем файла; сырой текст WIC — только в <see cref="AgrReadException.Diagnostic"/>.
    /// </summary>
    public void Fill(string path, AgrTextureInfo info)
    {
        string file = Path.GetFileName(path);
        var sw = Stopwatch.StartNew();
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            ReadPngHeader(fs, info);
            fs.Position = 0;
            var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
                                           BitmapCacheOption.None);
            BitmapSource frame = dec.Frames[0];
            info.W = frame.PixelWidth;
            info.H = frame.PixelHeight;
            PixelFormat fmt = frame.Format;
            info.PixelFormat = fmt.ToString();
            if (fmt == PixelFormats.Rgb48 || fmt == PixelFormats.Rgba64 || fmt == PixelFormats.Gray16)
            {
                Stats16(frame, fmt, info);
            }
            else if (fmt.BitsPerPixel <= 32)
            {
                bool alpha = fmt == PixelFormats.Bgra32 || fmt == PixelFormats.Pbgra32
                             || (frame.Palette != null && frame.Palette.Colors.Any(c => c.A != 255));
                BitmapSource src = fmt == PixelFormats.Bgra32 ? frame : new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
                Stats8(src, alpha, info);
            }
            else
            {
                throw new AgrReadException(file, $"Текстура {file}: формат пикселей {fmt} не поддержан.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw AgrReadException.Unreadable("Текстура", file, ex);
        }
        catch (Exception ex) when (ex is FormatException or NotSupportedException or COMException or ArgumentException
                                       or OverflowException or InvalidOperationException)
        {
            throw new AgrReadException(file, $"Текстура {file} не читается: файл повреждён или это не PNG.", ex);
        }
        info.Analyzed = true;
        info.Ms = Math.Round(sw.Elapsed.TotalMilliseconds, 1);
    }

    static void ReadPngHeader(Stream s, AgrTextureInfo info)
    {
        Span<byte> h = stackalloc byte[26];
        int n = 0;
        while (n < h.Length)
        {
            int r = s.Read(h[n..]);
            if (r <= 0) break;
            n += r;
        }
        // сигнатура PNG + длина + "IHDR" + ширина + высота + битность + тип цвета
        if (n == 26 && h[0] == 0x89 && h[1] == (byte)'P' && h[12] == (byte)'I' && h[13] == (byte)'H')
        {
            info.BitDepth = h[24];
            info.ColorType = h[25];
        }
    }

    // Буфер пикселей — на вызов: общий буфер в экземпляре при чтении из нескольких потоков смешивал пиксели разных
    // картинок (ревью C1a: 19–34 неверных отчёта из 64 без единого исключения).
    byte[] RentPixels(long len, string file)
    {
        if (len > Array.MaxLength)
        {
            throw new AgrReadException(file, $"Текстура {file}: картинка больше 2 ГБ в памяти — не поддержана.");
        }
        return ArrayPool<byte>.Shared.Rent((int)len);
    }

    void ReturnPixels(byte[] buf) => ArrayPool<byte>.Shared.Return(buf);

    unsafe void Stats8(BitmapSource src, bool hasAlpha, AgrTextureInfo info)
    {
        int w = info.W, h = info.H;
        int stride = w * 4;
        byte[] buf = RentPixels((long)stride * h, info.File);
        try
        {
            src.CopyPixels(buf, stride, 0);
            Stats8Core(buf, hasAlpha, info);
        }
        finally
        {
            ReturnPixels(buf);
        }
    }

    static unsafe void Stats8Core(byte[] buf, bool hasAlpha, AgrTextureInfo info)
    {
        int w = info.W, h = info.H;
        long n = (long)w * h;
        long sR = 0, sG = 0, sB = 0, sA = 0, aMid = 0, aLow = 0, rHigh = 0;
        int mnR = 255, mnG = 255, mnB = 255, mnA = 255, mxR = 0, mxG = 0, mxB = 0;
        fixed (byte* p0 = buf)
        {
            byte* p = p0;
            for (long i = 0; i < n; i++, p += 4)
            {
                int b = p[0], g = p[1], r = p[2], a = p[3];
                sR += r; sG += g; sB += b; sA += a;
                if (r < mnR) mnR = r;
                if (r > mxR) mxR = r;
                if (g < mnG) mnG = g;
                if (g > mxG) mxG = g;
                if (b < mnB) mnB = b;
                if (b > mxB) mxB = b;
                if (a < mnA) mnA = a;
                if (a >= 6 && a <= 249) aMid++;      // 0,02 < a < 0,98
                if (a < 128) aLow++;                  // a < 0,5
                if (r >= 13) rHigh++;                 // r > 0,05
            }
        }
        double[] mean = { sR / (double)n / 255.0, sG / (double)n / 255.0, sB / (double)n / 255.0 };
        SetColor(info, mean, new[] { mnR, mnG, mnB }, new[] { mxR, mxG, mxB },
                 Math.Max(mxR - mnR, Math.Max(mxG - mnG, mxB - mnB)));
        info.HasAlpha = hasAlpha;
        if (hasAlpha)
        {
            info.Alpha = MakeAlpha(sA / (double)n / 255.0, mnA, aLow / (double)n, aMid / (double)n);
        }
        if (info.Kind == "ERM")
        {
            info.ErmRed = MakeErm(info, mean[0] * 255.0, mxR, rHigh / (double)n);
        }
    }

    void Stats16(BitmapSource frame, PixelFormat fmt, AgrTextureInfo info)
    {
        int ch = fmt == PixelFormats.Gray16 ? 1 : fmt == PixelFormats.Rgb48 ? 3 : 4;
        int stride = info.W * ch * 2;
        byte[] buf = RentPixels((long)stride * info.H, info.File);
        try
        {
            frame.CopyPixels(buf, stride, 0);
            Stats16Core(MemoryMarshal.Cast<byte, ushort>(buf.AsSpan(0, stride * info.H)), ch, info);
        }
        finally
        {
            ReturnPixels(buf);
        }
    }

    static void Stats16Core(ReadOnlySpan<ushort> px, int ch, AgrTextureInfo info)
    {
        int w = info.W, h = info.H;
        long n = (long)w * h;
        const double Max = 65535.0;
        long sR = 0, sG = 0, sB = 0, sA = 0, aMid = 0, aLow = 0, rHigh = 0;
        int mnR = 65535, mnG = 65535, mnB = 65535, mnA = 65535, mxR = 0, mxG = 0, mxB = 0;
        // пороги как в 8-битной ветке, но по v/65535
        const double MidLo = 0.02 * Max, MidHi = 0.98 * Max, Half = 0.5 * Max, RHigh = 0.05 * Max;
        for (long i = 0; i < n; i++)
        {
            int o = (int)(i * ch);
            int r = px[o];
            int g = ch == 1 ? r : px[o + 1];
            int b = ch == 1 ? r : px[o + 2];
            int a = ch == 4 ? px[o + 3] : 65535;
            sR += r; sG += g; sB += b; sA += a;
            if (r < mnR) mnR = r;
            if (r > mxR) mxR = r;
            if (g < mnG) mnG = g;
            if (g > mxG) mxG = g;
            if (b < mnB) mnB = b;
            if (b > mxB) mxB = b;
            if (a < mnA) mnA = a;
            if (a > MidLo && a < MidHi) aMid++;
            if (a < Half) aLow++;
            if (r > RHigh) rHigh++;
        }
        double[] mean = { sR / (double)n / Max, sG / (double)n / Max, sB / (double)n / Max };
        int To255(int v) => (int)Math.Round(v / Max * 255.0, MidpointRounding.ToEven);
        double span = Math.Max(mxR - mnR, Math.Max(mxG - mnG, mxB - mnB)) / Max * 255.0;
        SetColor(info, mean, new[] { To255(mnR), To255(mnG), To255(mnB) }, new[] { To255(mxR), To255(mxG), To255(mxB) }, span);
        info.HasAlpha = ch == 4;
        if (info.HasAlpha)
        {
            info.Alpha = MakeAlpha(sA / (double)n / Max, To255(mnA), aLow / (double)n, aMid / (double)n);
        }
        if (info.Kind == "ERM")
        {
            info.ErmRed = MakeErm(info, mean[0] * 255.0, To255(mxR), rHigh / (double)n);
        }
    }

    static void SetColor(AgrTextureInfo info, double[] mean, int[] min255, int[] max255, double span255)
    {
        info.Mean = Array.ConvertAll(mean, v => Math.Round(v, 6));
        info.Min255 = min255;
        info.Max255 = max255;
        info.Span255 = Math.Round(span255, 4);
        info.Uniform = span255 <= AgrRules.UniformSpan255 + 1e-6;
        info.Rgb255 = Array.ConvertAll(mean, v => (int)Math.Round(v * 255.0, MidpointRounding.ToEven));
    }

    static AgrAlphaStats MakeAlpha(double mean, int min255, double fracLow, double fracMid) => new()
    {
        Mean = Math.Round(mean, 6),
        Min255 = min255,
        FracBelowHalf = Math.Round(fracLow, 6),
        FracMid = Math.Round(fracMid, 6),
        NonTrivial = min255 < 254,
        Mode = min255 >= 254 ? "OPAQUE" : fracMid < 0.05 ? "MASK" : "BLEND",
    };

    static AgrErmRedStats MakeErm(AgrTextureInfo info, double mean255, int max255, double fracHigh) => new()
    {
        Mean255 = Math.Round(mean255, 3),
        Max255 = max255,
        FracAbove005 = Math.Round(fracHigh, 6),
        CarriesEmissive = info.Uniform ? info.Rgb255[0] > 12 : fracHigh > 0.001,
    };
}
