using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Connector.Upgrade.WindowsPayload;

internal static class WindowsPathSafety
{
    public static string NormalizeExistingRegularFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A payload path is required.", nameof(path));

        var fullPath = Path.GetFullPath(path);
        AssertNoReparseComponents(fullPath);
        var attributes = File.GetAttributes(fullPath);
        if ((attributes & FileAttributes.Directory) != 0 ||
            (attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"Rollback payload '{fullPath}' must be a regular non-reparse file.");
        return fullPath;
    }

    public static void AssertNoReparseComponents(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root))
            throw new InvalidDataException($"Rollback path '{path}' has no filesystem root.");

        var current = root;
        AssertNotReparse(current, fullPath);
        foreach (var component in fullPath[root.Length..].Split(
                     Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            AssertNotReparse(current, fullPath);
        }
    }

    public static void AssertHandleMatchesPath(SafeFileHandle handle, string expectedPath)
    {
        if (handle.IsInvalid || handle.IsClosed)
            throw new ObjectDisposedException(nameof(handle));

        var attributeInfo = new FileAttributeTagInfo();
        if (!GetFileInformationByHandleEx(
                handle,
                FileInfoByHandleClass.FileAttributeTagInfo,
                ref attributeInfo,
                (uint)Marshal.SizeOf<FileAttributeTagInfo>()))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not inspect the opened rollback payload.");
        if ((attributeInfo.FileAttributes & FileAttributes.ReparsePoint) != 0 || attributeInfo.ReparseTag != 0)
            throw new InvalidDataException("The opened rollback payload is a reparse point.");

        var finalPath = GetFinalPath(handle);
        if (!string.Equals(
                Path.GetFullPath(expectedPath).TrimEnd(Path.DirectorySeparatorChar),
                finalPath.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The opened rollback payload resolved to a different filesystem path.");
    }

    private static void AssertNotReparse(string path, string requestedPath)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"Rollback path '{requestedPath}' contains a reparse-point component.");
    }

    private static string GetFinalPath(SafeFileHandle handle)
    {
        var capacity = 512;
        while (true)
        {
            var buffer = new StringBuilder(capacity);
            var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not resolve the opened rollback payload path.");
            if (length < buffer.Capacity)
                return RemoveExtendedPrefix(buffer.ToString());
            capacity = checked((int)length + 1);
        }
    }

    private static string RemoveExtendedPrefix(string path)
    {
        const string uncPrefix = @"\\?\UNC\";
        const string localPrefix = @"\\?\";
        if (path.StartsWith(uncPrefix, StringComparison.OrdinalIgnoreCase))
            return @"\\" + path[uncPrefix.Length..];
        return path.StartsWith(localPrefix, StringComparison.OrdinalIgnoreCase)
            ? path[localPrefix.Length..]
            : path;
    }

    private enum FileInfoByHandleClass
    {
        FileAttributeTagInfo = 9,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInfo
    {
        public FileAttributes FileAttributes;
        public uint ReparseTag;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        FileInfoByHandleClass fileInformationClass,
        ref FileAttributeTagInfo fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        StringBuilder filePath,
        uint filePathLength,
        uint flags);
}
