using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Connector.Upgrade.HelperReleaseTrust;

namespace Connector.Upgrade.LaunchBridge;

internal static class Program
{
    private const int ElevatedTokenInformationClass = 20;
    private const int IntegrityLevelInformationClass = 25;
    private const int HighIntegrityRid = 0x3000;
    private const long MaximumCallerImageBytes = 1024L * 1024 * 1024;
    private static readonly HashSet<string> TrustedOwners = new(StringComparer.Ordinal)
    {
        new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value,
        new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value,
        new SecurityIdentifier("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464").Value
    };
    private const FileSystemRights MutationRights = (FileSystemRights)0x10000000 | (FileSystemRights)0x40000000 |
        FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteAttributes |
        FileSystemRights.WriteExtendedAttributes | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
        FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            if (args.Length != 0) throw new SecurityException("The launch bridge accepts no arguments.");
            AssertUnelevatedCurrentToken();

            // The manifest, its detached signature, and the expected relative image path are all
            // loaded from protected machine locations. No caller path, SID, or token arrives via
            // the command line or environment.
            var signedPin = new HelperReleasePinSource().GetCallerImagePin()
                ?? throw new SecurityException("A signed schema-v4 caller image pin is required.");
            if (!string.Equals(signedPin.RelativePath, ProtectedCallerImagePin.ExpectedRelativePath, StringComparison.Ordinal) ||
                signedPin.Size is <= 0 or > MaximumCallerImageBytes ||
                signedPin.Sha256.Length != 64 || !signedPin.Sha256.All(Uri.IsHexDigit))
                throw new SecurityException("The signed caller image pin is invalid.");

            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (string.IsNullOrWhiteSpace(programFiles)) throw new SecurityException("Program Files is unavailable.");
            var root = Path.GetFullPath(programFiles);
            var imagePath = Path.GetFullPath(Path.Combine(root, signedPin.RelativePath));
            var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!imagePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(imagePath, Path.Combine(root, "StructuraConnectorInstaller", "Bootstrapper", "Connector.Upgrade.Bootstrapper.exe"), StringComparison.OrdinalIgnoreCase))
                throw new SecurityException("The pinned caller path is outside the fixed protected installation location.");

            // Keep the verified image opened without write/delete sharing across process creation.
            // This closes the validate-then-replace window between hashing and CreateProcess.
            using var verifiedImage = OpenProtectedImage(root, imagePath, signedPin.Size, signedPin.Sha256);
            var workingDirectory = Path.GetDirectoryName(imagePath)!;
            using var child = Process.Start(new ProcessStartInfo
            {
                FileName = imagePath,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = false,
                WindowStyle = ProcessWindowStyle.Normal
            }) ?? throw new InvalidOperationException("The installed caller process did not start.");

            // CreateProcess without shell elevation or a different logon uses this exact token.
            // Confirm the process remains live and has the same account before waiting on it.
            using var current = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
            if (!OpenProcessToken(child.Handle, 0x0008, out var childToken))
                throw new SecurityException("The installed caller token could not be inspected.");
            using var ownedChildToken = childToken;
            using var launched = new WindowsIdentity(ownedChildToken.DangerousGetHandle());
            if (current.User is null || launched.User is null || !current.User.Equals(launched.User))
                throw new SecurityException("The installed caller did not inherit the original user identity.");
            AssertTokenUnelevated(ownedChildToken.DangerousGetHandle());
            child.WaitForExit();
            return child.ExitCode;
        }
        catch
        {
            // Do not surface file, signature, or process details through the installer UI/log.
            return 1;
        }
    }

    private static void AssertUnelevatedCurrentToken()
    {
        using var current = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        if (!current.IsAuthenticated || current.User is null ||
            current.User.Equals(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)))
            throw new SecurityException("The bridge requires an interactive authenticated user.");
        AssertTokenUnelevated(current.Token);
    }

    private static void AssertTokenUnelevated(IntPtr token)
    {
        var elevation = ReadTokenValue(token, ElevatedTokenInformationClass);
        if (BitConverter.ToInt32(elevation) != 0)
            throw new SecurityException("The bridge and its caller must not be elevated.");

        var label = ReadTokenBuffer(token, IntegrityLevelInformationClass);
        try
        {
            var sid = Marshal.ReadIntPtr(label);
            var value = new SecurityIdentifier(sid).Value;
            var rid = int.Parse(value[(value.LastIndexOf('-') + 1)..], System.Globalization.CultureInfo.InvariantCulture);
            if (rid >= HighIntegrityRid) throw new SecurityException("High-integrity launch context is not allowed.");
        }
        finally { Marshal.FreeHGlobal(label); }
    }

    private static byte[] ReadTokenValue(IntPtr token, int informationClass)
    {
        var buffer = ReadTokenBuffer(token, informationClass);
        try { return BitConverter.GetBytes(Marshal.ReadInt32(buffer)); }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static IntPtr ReadTokenBuffer(IntPtr token, int informationClass)
    {
        _ = GetTokenInformation(token, informationClass, IntPtr.Zero, 0, out var required);
        if (required <= 0) throw new SecurityException("Token information is unavailable.");
        var buffer = Marshal.AllocHGlobal(checked((int)required));
        if (!GetTokenInformation(token, informationClass, buffer, required, out _))
        {
            Marshal.FreeHGlobal(buffer);
            throw new SecurityException("Token information could not be read.");
        }
        return buffer;
    }

    private static FileStream OpenProtectedImage(string root, string imagePath, long expectedSize, string expectedHash)
    {
        var programFiles = Path.GetFullPath(root);
        _ = Path.GetPathRoot(programFiles) ?? throw new SecurityException("Program Files root is invalid.");
        if (!PathEquals(programFiles, imagePath) && !imagePath.StartsWith(programFiles.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new SecurityException("Caller image is outside Program Files.");

        // The drive root is intentionally excluded: Windows grants Authenticated Users
        // CreateDirectories there. Program Files itself and every descendant must be protected.
        var rootAttributes = File.GetAttributes(programFiles);
        if ((rootAttributes & FileAttributes.Directory) == 0 || (rootAttributes & FileAttributes.ReparsePoint) != 0)
            throw new SecurityException("Program Files is not a protected ordinary directory.");
        AssertNoUntrustedMutation(programFiles, directory: true);

        var current = programFiles;
        var segments = Path.GetRelativePath(programFiles, imagePath).Split(Path.DirectorySeparatorChar);
        for (var index = 0; index < segments.Length; index++)
        {
            current = Path.Combine(current, segments[index]);
            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new SecurityException("Reparse points are not allowed in the caller image path.");
            var isDirectory = (attributes & FileAttributes.Directory) != 0;
            if (isDirectory != (index < segments.Length - 1))
                throw new SecurityException("Caller image path contains a missing or unexpected file component.");
            AssertNoUntrustedMutation(current, isDirectory);
        }

        if (segments.Length < 1 || Path.IsPathRooted(Path.GetRelativePath(programFiles, imagePath)))
            throw new SecurityException("Caller image path is invalid.");

        FileStream? stream = null;
        try
        {
            stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
            var finalPath = GetFinalPath(stream.SafeFileHandle);
            if (!PathEquals(finalPath, imagePath)) throw new SecurityException("Opened caller image does not resolve to the pinned path.");
            if (stream.Length != expectedSize) throw new SecurityException("Caller image size does not match its signed pin.");
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(stream)), expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new SecurityException("Caller image does not match its signed pin.");
            return stream;
        }
        catch
        {
            stream?.Dispose();
            throw;
        }
    }

    private static void AssertNoUntrustedMutation(string path, bool directory)
    {
        FileSystemSecurity security = directory
            ? FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(path), AccessControlSections.Owner | AccessControlSections.Access)
            : FileSystemAclExtensions.GetAccessControl(new FileInfo(path), AccessControlSections.Owner | AccessControlSections.Access);
        if (!TrustedOwners.Contains(security.GetOwner(typeof(SecurityIdentifier))?.Value ?? string.Empty))
            throw new SecurityException("Caller image path has an untrusted owner.");
        if (!security.AreAccessRulesProtected)
            throw new SecurityException("Caller image path inherits its access rules.");
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow &&
                (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0 &&
                !TrustedOwners.Contains(rule.IdentityReference.Value) && (rule.FileSystemRights & MutationRights) != 0)
                throw new SecurityException("Caller image path grants mutation rights to an untrusted principal.");
    }

    private static string GetFinalPath(Microsoft.Win32.SafeHandles.SafeFileHandle handle)
    {
        var path = new System.Text.StringBuilder(32768);
        var length = GetFinalPathNameByHandle(handle, path, (uint)path.Capacity, 0);
        if (length == 0 || length >= path.Capacity) throw new SecurityException("Caller image final path could not be verified.");
        var value = path.ToString();
        return value.StartsWith("\\\\?\\", StringComparison.Ordinal) ? value[4..] : value;
    }

    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out Microsoft.Win32.SafeHandles.SafeAccessTokenHandle tokenHandle);

    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass, IntPtr tokenInformation,
        uint tokenInformationLength, out uint returnLength);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle file, System.Text.StringBuilder path,
        uint pathLength, uint flags);

    private static bool PathEquals(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
}
