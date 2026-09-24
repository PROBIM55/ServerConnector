using System.IO.Compression;

namespace Connector.AgrConversion;

public enum AgrSourceKind
{
    Directory,
    Zip,
}

/// <summary>Одна часть пакета: папка с SM_*.fbx или zip части.</summary>
public sealed record AgrPartSource(string PartName, AgrSourceKind Kind, string Path);

/// <summary>
/// Чтение пакета АГР: вход — папка или zip одной части, папка всего пакета (zip частей и/или папки частей) или
/// набор zip. Zip распаковывается во временную папку под рабочим корнем и удаляется в finally — в том числе при
/// ошибке чтения (требование C1 (б)).
/// <para>
/// Потокобезопасность: экземпляр неизменяем (<see cref="WorkRoot"/>), <see cref="ReadPart"/> и
/// <see cref="WithPartDirectory{T}"/> можно вызывать из нескольких потоков — у каждой распаковки своя папка
/// <c>agr-unzip-&lt;guid&gt;</c>. Один <see cref="AgrPartReader"/> на все потоки допустим: он без состояния.
/// </para>
/// Ошибки для пользователя — <see cref="AgrReadException"/>: по-русски, с именем архива или файла.
/// </summary>
public sealed class AgrPackageReader
{
    public AgrPackageReader(string workRoot)
    {
        WorkRoot = System.IO.Path.GetFullPath(workRoot);
    }

    public string WorkRoot { get; }

    /// <summary>Префикс временных папок распаковки под <see cref="WorkRoot"/>.</summary>
    public const string ExtractPrefix = "agr-unzip-";

    /// <summary>Главный FBX части: SM_*.fbx, кроме *_Light.fbx (свет не переносится).</summary>
    public static bool IsMainFbx(string fileName) =>
        fileName.StartsWith("SM_", StringComparison.OrdinalIgnoreCase)
        && fileName.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)
        && !IsLightFbx(fileName);

    public static bool IsLightFbx(string fileName) =>
        fileName.EndsWith("_Light.fbx", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<AgrPartSource> Discover(params string[] inputs) => Discover((IEnumerable<string>)inputs);

    public static IReadOnlyList<AgrPartSource> Discover(IEnumerable<string> inputs)
    {
        var list = new List<AgrPartSource>();
        foreach (var raw in inputs)
        {
            string input = System.IO.Path.GetFullPath(raw);
            if (File.Exists(input))
            {
                if (!input.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"{input}: ожидается zip части или папка");
                }
                list.Add(new AgrPartSource(System.IO.Path.GetFileNameWithoutExtension(input), AgrSourceKind.Zip, input));
                continue;
            }
            if (!Directory.Exists(input))
            {
                throw new FileNotFoundException($"{input}: нет такого файла или папки", input);
            }
            string? own = MainFbxIn(input);
            if (own != null)
            {
                list.Add(new AgrPartSource(System.IO.Path.GetFileNameWithoutExtension(own), AgrSourceKind.Directory, input));
                continue;
            }
            int before = list.Count;
            foreach (var zip in Directory.EnumerateFiles(input, "*.zip").OrderBy(p => p, StringComparer.Ordinal))
            {
                list.Add(new AgrPartSource(System.IO.Path.GetFileNameWithoutExtension(zip), AgrSourceKind.Zip, zip));
            }
            foreach (var dir in Directory.EnumerateDirectories(input).OrderBy(p => p, StringComparer.Ordinal))
            {
                string? fbx = MainFbxIn(dir);
                if (fbx != null)
                {
                    list.Add(new AgrPartSource(System.IO.Path.GetFileNameWithoutExtension(fbx), AgrSourceKind.Directory, dir));
                }
            }
            if (list.Count == before)
            {
                throw new InvalidDataException($"{input}: не найдено ни части (SM_*.fbx), ни zip");
            }
        }
        var dup = list.GroupBy(s => s.PartName, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (dup != null)
        {
            throw new InvalidDataException($"часть {dup.Key} встречается несколько раз: {string.Join("; ", dup.Select(s => s.Path))}");
        }
        return list.OrderBy(s => s.PartName, StringComparer.Ordinal).ToList();
    }

    static string? MainFbxIn(string dir)
    {
        var fbx = Directory.EnumerateFiles(dir, "*.fbx").Where(p => IsMainFbx(System.IO.Path.GetFileName(p))).ToList();
        if (fbx.Count > 1)
        {
            throw new InvalidDataException($"{dir}: несколько главных FBX: {string.Join(", ", fbx.Select(System.IO.Path.GetFileName))}");
        }
        return fbx.Count == 1 ? fbx[0] : null;
    }

    /// <summary>
    /// Выполняет <paramref name="body"/> над папкой части. Zip распаковывается под <see cref="WorkRoot"/> и
    /// удаляется после <paramref name="body"/> — и при успехе, и при ошибке.
    /// </summary>
    public T WithPartDirectory<T>(AgrPartSource source, Func<string, T> body)
    {
        if (source.Kind == AgrSourceKind.Directory)
        {
            return body(source.Path);
        }
        Directory.CreateDirectory(WorkRoot);
        string dir = System.IO.Path.Combine(WorkRoot, ExtractPrefix + Guid.NewGuid().ToString("N")[..12]);
        bool completed = false;
        try
        {
            ExtractZip(source.Path, dir);
            T result = body(FindPartRoot(dir, System.IO.Path.GetFileName(source.Path)));
            completed = true;
            return result;
        }
        finally
        {
            DeleteExtraction(dir, throwOnFailure: completed);
        }
    }

    public AgrPart ReadPart(AgrPartSource source, AgrPartReader? reader = null)
    {
        var r = reader ?? new AgrPartReader();
        string description = source.Kind == AgrSourceKind.Zip ? "zip " + System.IO.Path.GetFileName(source.Path) : source.Path;
        return WithPartDirectory(source, dir => r.Read(dir, description));
    }

    /// <summary>
    /// Распаковка с защитой от путей вне папки назначения. Битый или занятый архив, запись за пределы папки —
    /// <see cref="AgrReadException"/> с именем архива; сырой текст System.IO.Compression — только в диагностике.
    /// </summary>
    public static void ExtractZip(string zipPath, string destination)
    {
        string zipName = System.IO.Path.GetFileName(zipPath);
        string destFull = System.IO.Path.GetFullPath(destination);
        Directory.CreateDirectory(destFull);
        string prefix = destFull.EndsWith(System.IO.Path.DirectorySeparatorChar) ? destFull : destFull + System.IO.Path.DirectorySeparatorChar;
        string? current = null;
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            foreach (var entry in zip.Entries)
            {
                current = entry.FullName;
                string target = System.IO.Path.GetFullPath(System.IO.Path.Combine(destFull, entry.FullName));
                if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    throw new AgrReadException(zipName, $"Архив {zipName}: запись {entry.FullName} ведёт за пределы папки распаковки — архив отклонён.");
                }
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                {
                    Directory.CreateDirectory(target);
                    continue;
                }
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: false);
            }
        }
        catch (InvalidDataException ex)
        {
            throw new AgrReadException(zipName, current == null
                ? $"Архив {zipName} повреждён или это не zip — распаковать не удалось."
                : $"Архив {zipName}: запись {current} повреждена — распаковать не удалось.", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw current == null
                ? AgrReadException.Unreadable("Архив части", zipName, ex)
                : new AgrReadException(zipName, $"Архив {zipName}: запись {current} не распаковалась в рабочую папку.", ex);
        }
    }

    /// <summary>Папка части внутри распаковки: единственная папка с главным SM_*.fbx. <paramref name="archiveName"/> — для текста ошибки.</summary>
    public static string FindPartRoot(string extracted, string? archiveName = null)
    {
        var roots = new List<string>();
        if (MainFbxIn(extracted) != null) roots.Add(extracted);
        foreach (var dir in Directory.EnumerateDirectories(extracted, "*", SearchOption.AllDirectories))
        {
            if (MainFbxIn(dir) != null) roots.Add(dir);
        }
        string where = archiveName == null ? $"В папке {extracted}" : $"В архиве {archiveName}";
        return roots.Count switch
        {
            1 => roots[0],
            0 => throw new AgrReadException(archiveName ?? extracted, $"{where} нет части АГР — главного файла SM_*.fbx."),
            _ => throw new AgrReadException(archiveName ?? extracted, $"{where} несколько частей АГР: "
                + string.Join(", ", roots.Select(r => System.IO.Path.GetRelativePath(extracted, r))) + "."),
        };
    }

    /// <summary>Удаление распаковки с повторами (антивирус/индексатор держат файлы). Без исключения, если уже летит ошибка чтения.</summary>
    public static void DeleteExtraction(string dir, bool throwOnFailure)
    {
        Exception? last = null;
        for (int attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                if (!Directory.Exists(dir)) return;
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    var attr = File.GetAttributes(f);
                    if ((attr & FileAttributes.ReadOnly) != 0) File.SetAttributes(f, attr & ~FileAttributes.ReadOnly);
                }
                Directory.Delete(dir, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                last = ex;
                Thread.Sleep(100 * (attempt + 1));
            }
        }
        if (throwOnFailure && last != null)
        {
            throw new IOException($"Не удалось удалить временную распаковку {dir}: файлы держит другой процесс — папку можно удалить вручную.", last);
        }
    }
}
