using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Connector.Upgrade.UserState;

internal static class DurableFilePublication
{
    private const uint MoveFileReplaceExisting = 0x1;
    private const uint MoveFileWriteThrough = 0x8;

    public static void Publish(string temporaryPath, string destinationPath)
    {
        if (OperatingSystem.IsWindows())
        {
            var flags = MoveFileWriteThrough;
            if (File.Exists(destinationPath)) flags |= MoveFileReplaceExisting;
            if (!MoveFileExW(temporaryPath, destinationPath, flags))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Durable journal publication failed.");
            }

            return;
        }

        // The product path is Windows. This fallback keeps isolated fixtures portable;
        // it is not used to claim physical power-loss durability on another OS.
        File.Move(temporaryPath, destinationPath, overwrite: File.Exists(destinationPath));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileExW(string existingFileName, string newFileName, uint flags);
}
