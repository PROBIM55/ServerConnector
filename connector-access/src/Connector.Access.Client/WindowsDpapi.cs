using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Connector.Access.Client;

internal static class WindowsDpapi
{
    private const int CryptProtectUiForbidden = 0x1;

    public static byte[] ProtectCurrentUser(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> entropy) =>
        Transform(plaintext, entropy, protect: true);

    public static byte[] UnprotectCurrentUser(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> entropy) =>
        Transform(ciphertext, entropy, protect: false);

    private static byte[] Transform(ReadOnlySpan<byte> input, ReadOnlySpan<byte> entropy, bool protect)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Connector private keys require Windows DPAPI CurrentUser.");
        }

        var inputBytes = input.ToArray();
        var entropyBytes = entropy.ToArray();
        var inputHandle = GCHandle.Alloc(inputBytes, GCHandleType.Pinned);
        var entropyHandle = GCHandle.Alloc(entropyBytes, GCHandleType.Pinned);
        var inputBlob = new DataBlob(inputBytes.Length, inputHandle.AddrOfPinnedObject());
        var entropyBlob = new DataBlob(entropyBytes.Length, entropyHandle.AddrOfPinnedObject());
        DataBlob outputBlob = default;
        try
        {
            var succeeded = protect
                ? CryptProtectData(ref inputBlob, null, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out outputBlob)
                : CryptUnprotectData(ref inputBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out outputBlob);
            if (!succeeded)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows DPAPI rejected connector enrollment key material.");
            }

            var output = new byte[outputBlob.Length];
            Marshal.Copy(outputBlob.Data, output, 0, output.Length);
            return output;
        }
        finally
        {
            if (outputBlob.Data != IntPtr.Zero)
            {
                LocalFree(outputBlob.Data);
            }

            inputHandle.Free();
            entropyHandle.Free();
            CryptographicOperations.ZeroMemory(inputBytes);
            CryptographicOperations.ZeroMemory(entropyBytes);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public DataBlob(int length, IntPtr data)
        {
            Length = length;
            Data = data;
        }

        public int Length;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string? description,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr description,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
