using System.IO.Compression;

namespace Connector.AgrConversion.Service;

/// <summary>Кандидат во вход очереди: zip части или папка распакованной части; <see cref="Error"/> — не часть АГР.</summary>
public sealed record AgrInputItem(string Path, string Name, long Bytes, string? Error);

/// <summary>
/// Разбор ввода: zip частей как пришли, папка с zip частей или несколько файлов. Не часть АГР — строка с понятной
/// ошибкой («в архиве нет FBX», «не архив АГР»), не исключение.
/// </summary>
public static class AgrConverterInput
{
    public static IReadOnlyList<AgrInputItem> Collect(IEnumerable<string> paths)
    {
        var list = new List<AgrInputItem>();
        foreach (var raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string path;
            try
            {
                path = System.IO.Path.GetFullPath(raw.Trim().Trim('"'));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                list.Add(new AgrInputItem(raw, raw, 0, $"{raw}: неверный путь"));
                continue;
            }
            if (File.Exists(path))
            {
                list.Add(FromFile(path));
            }
            else if (Directory.Exists(path))
            {
                list.AddRange(FromDirectory(path));
            }
            else
            {
                list.Add(new AgrInputItem(path, AgrConverterPaths.PartName(path), 0, $"{System.IO.Path.GetFileName(path)}: нет такого файла или папки"));
            }
        }
        return list;
    }

    static AgrInputItem FromFile(string path)
    {
        string name = AgrConverterPaths.PartName(path);
        long bytes = SafeLength(path);
        return new AgrInputItem(path, name, bytes, CheckZip(path));
    }

    static IEnumerable<AgrInputItem> FromDirectory(string dir)
    {
        string? error = null;
        List<AgrInputItem> items = new();
        try
        {
            if (MainFbxCount(dir) > 0)
            {
                // Папка распакованной части.
                return new[] { new AgrInputItem(dir, AgrConverterPaths.PartName(dir), DirectoryBytes(dir),
                    MainFbxCount(dir) > 1 ? $"{System.IO.Path.GetFileName(dir)}: в папке несколько главных FBX (SM_*.fbx) — нужна папка одной части" : null) };
            }
            foreach (var zip in Directory.EnumerateFiles(dir, "*.zip").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                if (zip.EndsWith(AgrConverterPaths.OutputSuffix, StringComparison.OrdinalIgnoreCase)) continue; // свои результаты
                items.Add(FromFile(zip));
            }
            foreach (var sub in Directory.EnumerateDirectories(dir).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                if (MainFbxCount(sub) == 1)
                {
                    items.Add(new AgrInputItem(sub, AgrConverterPaths.PartName(sub), DirectoryBytes(sub), null));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"{System.IO.Path.GetFileName(dir)}: папка не читается — {ex.Message}";
        }
        if (error == null && items.Count == 0)
        {
            error = $"{System.IO.Path.GetFileName(dir)}: в папке нет zip частей АГР";
        }
        return error == null ? items : new[] { new AgrInputItem(dir, AgrConverterPaths.PartName(dir), 0, error) };
    }

    /// <summary>
    /// Быстрая проверка zip без распаковки: открывается ли, есть ли FBX и ровно один главный <c>SM_*.fbx</c>. null — годен.
    /// </summary>
    public static string? CheckZip(string path)
    {
        string file = System.IO.Path.GetFileName(path);
        if (!path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return $"{file}: не архив АГР — нужен zip части";
        }
        if (path.EndsWith(AgrConverterPaths.OutputSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return $"{file}: это результат конвертера (.glb.zip), не архив АГР";
        }
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var fbx = zip.Entries
                .Select(e => e.FullName.Replace('\\', '/'))
                .Where(n => n.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (fbx.Count == 0)
            {
                return $"{file}: в архиве нет FBX — это не часть АГР";
            }
            int main = fbx.Count(n => AgrPackageReader.IsMainFbx(n[(n.LastIndexOf('/') + 1)..]));
            if (main == 0)
            {
                return $"{file}: в архиве нет главного FBX части (SM_*.fbx) — это не часть АГР";
            }
            if (main > 1)
            {
                return $"{file}: в архиве {main} главных FBX (SM_*.fbx) — нужен zip одной части";
            }
            return null;
        }
        catch (InvalidDataException)
        {
            return $"{file}: не архив АГР — файл не открывается как zip";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return AgrReadException.Unreadable("Архив части", file, ex).Message;
        }
    }

    static int MainFbxCount(string dir) =>
        Directory.EnumerateFiles(dir, "*.fbx").Count(p => AgrPackageReader.IsMainFbx(System.IO.Path.GetFileName(p)));

    static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    static long DirectoryBytes(string dir)
    {
        try { return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(SafeLength); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }
}

/// <summary>Конвертер одной части для службы: настоящий (<see cref="AgrGltfpackPartConverter"/>) или поддельный в тестах.</summary>
public interface IAgrPartConverter
{
    /// <param name="input">zip части или папка части.</param>
    /// <param name="outputDirectory">Куда положить <c>.glb.zip</c> (временная папка строки; в папку запуска переносит служба).</param>
    /// <param name="workRoot">Временная папка конвертера (внутри временной папки строки).</param>
    AgrConvertResult Convert(string input, string outputDirectory, string workRoot, IProgress<AgrConvertProgress> progress,
                             CancellationToken cancellationToken);
}

/// <summary>Настоящий конвертер: <see cref="AgrPartConverter.ConvertPart"/> с gltfpack.</summary>
public sealed class AgrGltfpackPartConverter : IAgrPartConverter
{
    readonly Func<AgrConvertOptions, AgrConvertOptions>? _tune;

    public AgrGltfpackPartConverter(string gltfpackPath, Func<AgrConvertOptions, AgrConvertOptions>? tune = null)
    {
        GltfpackPath = gltfpackPath;
        _tune = tune;
    }

    /// <summary>gltfpack рядом с exe Коннектора (кладёт MSI, C2c).</summary>
    public static string DefaultGltfpackPath => System.IO.Path.Combine(AppContext.BaseDirectory, "gltfpack.exe");

    public string GltfpackPath { get; }

    public AgrConvertResult Convert(string input, string outputDirectory, string workRoot, IProgress<AgrConvertProgress> progress,
                                    CancellationToken cancellationToken)
    {
        var options = new AgrConvertOptions { GltfpackPath = GltfpackPath, WorkRoot = workRoot };
        return AgrPartConverter.ConvertPart(input, outputDirectory, _tune?.Invoke(options) ?? options, progress, cancellationToken);
    }
}
