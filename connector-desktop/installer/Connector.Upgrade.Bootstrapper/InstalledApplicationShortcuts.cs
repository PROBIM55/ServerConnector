using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Connector.Upgrade.Velopack;

namespace Connector.Upgrade.Bootstrapper;

internal static class InstalledApplicationShortcuts
{
    private const string ShortcutName = "Платформа Коннектор.lnk";
    private static readonly string[] PreviousNames = ["Structura Connector.lnk", "Structura Connector (новый).lnk"];

    public static void Publish(string installRoot) => Publish(installRoot,
        [Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolderOption.Create),
         Environment.GetFolderPath(Environment.SpecialFolder.Programs, Environment.SpecialFolderOption.Create)]);

    internal static void Publish(string installRoot, IReadOnlyList<string> directories)
    {
        var launcher = Path.Combine(installRoot, UnifiedVelopackApplication.RootLauncher);
        var executable = Path.Combine(installRoot, "current", UnifiedVelopackApplication.MainExecutable);
        if (!File.Exists(launcher) || !File.Exists(executable))
            throw new InvalidOperationException("Не удалось создать ярлыки приложения. Восстановите установку.");
        foreach (var directory in directories)
        {
            if (string.IsNullOrWhiteSpace(directory))
                throw new InvalidOperationException("Не удалось найти папку для ярлыков приложения.");
            Directory.CreateDirectory(directory);
            Write(Path.Combine(directory, ShortcutName), launcher, executable, installRoot);
            foreach (var name in PreviousNames)
            {
                var previous = Path.Combine(directory, name);
                if (!File.Exists(previous)) continue;
                // A broken or foreign shortcut is not proof of ownership. Keep it,
                // and never let optional cleanup undo the successful installation.
                try
                {
                    var target = Environment.ExpandEnvironmentVariables(Read(previous).Target);
                    if (!string.IsNullOrWhiteSpace(target) && Path.IsPathFullyQualified(target) && Path.GetFullPath(target).StartsWith(
                            Path.TrimEndingDirectorySeparator(Path.GetFullPath(installRoot)) + Path.DirectorySeparatorChar,
                            StringComparison.OrdinalIgnoreCase))
                        File.Delete(previous);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or COMException or ArgumentException)
                {
                    // Preserve the untrusted or unreadable shortcut.
                }
            }
        }
    }

    // IShellLinkW avoids WScript.Shell's IDispatch/dynamic binder. The actual
    // ready installer failed in that binder after the desktop package succeeded.
    internal static void Write(string path, string target, string icon, string workingDirectory)
    {
        var pending = Path.Combine(Path.GetDirectoryName(path)!,
            ".platform-shortcut-" + Guid.NewGuid().ToString("N") + ".lnk");
        try
        {
            object native = new ShellLink();
            try
            {
                var link = (IShellLinkW)native;
                link.SetPath(target);
                link.SetArguments(string.Empty);
                link.SetWorkingDirectory(workingDirectory);
                link.SetDescription("Платформа Коннектор");
                link.SetIconLocation(icon, 0);
                ((IPersistFile)native).Save(pending, true);
            }
            finally { Marshal.FinalReleaseComObject(native); }
            // Release the persistence object before replacing an existing link.
            File.Move(pending, path, overwrite: true);
        }
        finally { if (File.Exists(pending)) File.Delete(pending); }
    }

    internal sealed record Shortcut(string Target, string Icon, int IconIndex, string Arguments, string WorkingDirectory);

    internal static Shortcut Read(string path)
    {
        object native = new ShellLink();
        try
        {
            ((IPersistFile)native).Load(path, 0); // STGM_READ; never resolve or launch the target.
            var link = (IShellLinkW)native;
            var target = new StringBuilder(32768);
            var icon = new StringBuilder(32768);
            var arguments = new StringBuilder(32768);
            var directory = new StringBuilder(32768);
            link.GetPath(target, target.Capacity, IntPtr.Zero, 4); // SLGP_RAWPATH; optional WIN32_FIND_DATA is null.
            link.GetIconLocation(icon, icon.Capacity, out var iconIndex);
            link.GetArguments(arguments, arguments.Capacity);
            link.GetWorkingDirectory(directory, directory.Capacity);
            return new Shortcut(target.ToString(), icon.ToString(), iconIndex, arguments.ToString(), directory.ToString());
        }
        finally { Marshal.FinalReleaseComObject(native); }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private sealed class ShellLink { }

    // Preserve native vtable order, including unused entries.
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int capacity, IntPtr findData, uint flags);
        void GetIDList(out IntPtr itemIdList);
        void SetIDList(IntPtr itemIdList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder text, int capacity);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string text);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int capacity);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int capacity);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out ushort hotkey);
        void SetHotkey(ushort hotkey);
        void GetShowCmd(out int show);
        void SetShowCmd(int show);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int capacity, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }
}
