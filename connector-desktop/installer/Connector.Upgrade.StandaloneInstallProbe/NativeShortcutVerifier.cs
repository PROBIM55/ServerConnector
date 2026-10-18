using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Connector.Upgrade.Velopack;

// This fixture independently checks the saved Unicode file format and asks the
// Windows property system to resolve the original Unicode-named link. WScript
// uses ANSI shortcut metadata on hosted Windows and cannot verify this branding.
internal static class NativeShortcutVerifier
{
    private const string Name = "Платформа Коннектор";
    private static readonly Encoding Unicode = new UnicodeEncoding(false, false, true);
    internal sealed record Metadata(string Target, string Description, string WorkingDirectory,
        string Arguments, string Icon, int IconIndex);

    internal static async Task VerifyAsync(string root, string desktop, string diagnosticPath)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            var count = 0;
            var stage = "files";
            try
            {
                foreach (var location in new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.Programs })
                {
                    var directory = Environment.GetFolderPath(location);
                    var path = Path.Combine(directory, Name + ".lnk");
                    if (string.IsNullOrWhiteSpace(directory) || !File.Exists(path) ||
                        File.Exists(Path.Combine(directory, "Structura Connector (новый).lnk")) ||
                        File.Exists(Path.Combine(directory, "Structura Connector.lnk")))
                        throw new InvalidOperationException("A branded shortcut is missing or an owned pilot shortcut remains.");
                    stage = "unicode-file-metadata";
                    using var file = File.OpenRead(path);
                    if (file.Length is < 76 or > 65536) throw new InvalidDataException("Shortcut size differs.");
                    var bytes = new byte[checked((int)file.Length)];
                    file.ReadExactly(bytes);
                    var metadata = ReadMetadata(bytes);
                    var target = Path.Combine(root, UnifiedVelopackApplication.RootLauncher);
                    if (!Same(metadata.Target, target) || !Same(metadata.Icon, desktop) || metadata.IconIndex != 0 ||
                        !Same(metadata.WorkingDirectory, root) || metadata.Arguments != "" || metadata.Description != Name)
                        throw new InvalidOperationException("Shortcut target, icon or application metadata differs.");
                    stage = "windows-unicode-property-system";
                    if (!Same(ReadWindowsTarget(path), target))
                        throw new InvalidOperationException("Windows resolved a different shortcut target.");
                    count++;
                }
                if (count != 2) throw new InvalidOperationException("Both native shortcuts were not independently confirmed.");
                File.WriteAllText(diagnosticPath, JsonSerializer.Serialize(new { valid = true, count,
                    reader = "Windows Unicode property system and independent MS-SHLLINK Unicode metadata" }));
                completion.TrySetResult();
            }
            catch (Exception error)
            {
                try { File.WriteAllText(diagnosticPath, JsonSerializer.Serialize(new { valid = false, count, stage,
                    type = error.GetType().FullName, hResult = error.HResult.ToString("X8") })); }
                catch { /* The failure is still propagated if evidence storage is unavailable. */ }
                completion.TrySetException(error);
            }
        }) { IsBackground = true };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private static bool Same(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    // MS-SHLLINK sections 2.1/2.3/2.4. No product reader, COM vtable, shell
    // resolution, code page conversion or target execution is reused here.
    internal static Metadata ReadMetadata(byte[] bytes)
    {
        void Require(int position, int length, int end)
        {
            if (position < 0 || length < 0 || end > bytes.Length || position > end || length > end - position)
                throw new InvalidDataException("Shortcut metadata exceeds its file bound.");
        }
        uint U32(int position) { Require(position, 4, bytes.Length); return BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position, 4)); }
        ushort U16(int position) { Require(position, 2, bytes.Length); return BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(position, 2)); }
        int Offset(uint value) => value <= 65536 ? (int)value : throw new InvalidDataException("Shortcut offset exceeds its bound.");
        Require(0, 76, bytes.Length);
        if (bytes.Length > 65536 || U32(0) != 76 || new Guid(bytes.AsSpan(4, 16)) != new Guid("00021401-0000-0000-C000-000000000046"))
            throw new InvalidDataException("Shortcut header differs.");
        var flags = U32(20);
        if ((flags & (0x80u | 0x02u | 0x04u | 0x10u | 0x40u)) != (0x80u | 0x02u | 0x04u | 0x10u | 0x40u))
            throw new InvalidDataException("Required Unicode shortcut metadata is missing.");
        var at = 76;
        if ((flags & 1) != 0) { var size = U16(at); Require(at + 2, size, bytes.Length); at += 2 + size; }
        var info = at;
        var infoLength = Offset(U32(info));
        var headerLength = Offset(U32(info + 4));
        Require(info, infoLength, bytes.Length);
        if (headerLength < 36 || headerLength > infoLength || (U32(info + 8) & 1) == 0)
            throw new InvalidDataException("Unicode target information is missing.");
        string InfoString(int field)
        {
            var offset = Offset(U32(info + field));
            if (offset < headerLength || offset >= infoLength) throw new InvalidDataException("Unicode target offset differs.");
            var begin = info + offset;
            var end = info + infoLength;
            var cursor = begin;
            while (cursor + 1 < end)
            {
                if (U16(cursor) == 0) return Unicode.GetString(bytes, begin, cursor - begin);
                cursor += 2;
            }
            throw new InvalidDataException("Unicode target is not terminated.");
        }
        var target = InfoString(28) + InfoString(32);
        at = info + infoLength;
        string Field(uint flag)
        {
            if ((flags & flag) == 0) return "";
            var characters = U16(at);
            at += 2;
            var length = characters * 2;
            Require(at, length, bytes.Length);
            var result = Unicode.GetString(bytes, at, length);
            at += length;
            return result;
        }
        var description = Field(4);
        _ = Field(8); // Relative path is layout-dependent; Windows resolves the real target below.
        var directory = Field(16);
        var arguments = Field(32);
        var icon = Field(64);
        return new Metadata(target, description, directory, arguments, icon, unchecked((int)U32(56)));
    }

    private static string ReadWindowsTarget(string path)
    {
        // Balance our own initialization for both S_OK and S_FALSE; never uninitialize
        // a failed attempt or depend on the CLR's lazy COM initialization.
        Marshal.ThrowExceptionForHR(CoInitializeEx(IntPtr.Zero, 2));
        try { return ReadInitializedWindowsTarget(path); }
        finally { CoUninitialize(); }
    }

    private static string ReadInitializedWindowsTarget(string path)
    {
        var iid = typeof(IPropertyStore).GUID;
        Marshal.ThrowExceptionForHR(SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, 0, ref iid, out var store));
        try
        {
            var value = Marshal.AllocCoTaskMem(24); // PROPVARIANT is 24 bytes on win-x64.
            try
            {
                for (var index = 0; index < 24; index++) Marshal.WriteByte(value, index, 0);
                var key = new PropertyKey { Format = new Guid("B9B4B3FC-2B51-4A42-B5D8-324146AFCF25"), Id = 2 };
                store.GetValue(ref key, value); // System.Link.TargetParsingPath, a Unicode string.
                if (Marshal.ReadInt16(value) != 31) throw new InvalidDataException("Windows shortcut target is not a Unicode string.");
                return Marshal.PtrToStringUni(Marshal.ReadIntPtr(value, 8)) ?? "";
            }
            finally { try { Marshal.ThrowExceptionForHR(PropVariantClear(value)); } finally { Marshal.FreeCoTaskMem(value); } }
        }
        finally { Marshal.FinalReleaseComObject(store); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct PropertyKey { public Guid Format; public uint Id; }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, IntPtr value);
        void SetValue(ref PropertyKey key, IntPtr value);
        void Commit();
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHGetPropertyStoreFromParsingName([MarshalAs(UnmanagedType.LPWStr)] string path,
        IntPtr context, uint flags, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);
    [DllImport("ole32.dll", ExactSpelling = true)] private static extern int PropVariantClear(IntPtr value);
    [DllImport("ole32.dll", ExactSpelling = true)] private static extern int CoInitializeEx(IntPtr reserved, uint flags);
    [DllImport("ole32.dll", ExactSpelling = true)] private static extern void CoUninitialize();
}
