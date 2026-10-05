using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using Connector.Upgrade.MachineAcl;
using System.Text;
using Connector.Upgrade.Velopack;
using Microsoft.Win32.SafeHandles;

namespace Connector.Upgrade.VelopackPayload;

public sealed class WindowsVelopackSetupStager : IWindowsVelopackSetupStager
{
    private readonly VelopackSetupSource _source;
    private readonly VelopackSetupPin _pin;
    private readonly VelopackSetupSignaturePolicy? _signaturePolicy;
    private readonly SecurityIdentifier _initiatingUser;
    private readonly string _root;
    private readonly string _batchDirectory;
    private readonly string _handlePrefix;
    private readonly VelopackSetupStagerHooks _hooks;
    private readonly bool _readOnlyExisting;

    public WindowsVelopackSetupStager(VelopackSetupSource source, VelopackSetupPin pin,
        VelopackSetupSignaturePolicy? signaturePolicy, string initiatingUserSid, string machineStagingRoot)
        : this(source, pin, signaturePolicy, initiatingUserSid, machineStagingRoot, null)
    {
    }

    internal WindowsVelopackSetupStager(VelopackSetupSource source, VelopackSetupPin pin,
        VelopackSetupSignaturePolicy? signaturePolicy, string initiatingUserSid, string machineStagingRoot,
        VelopackSetupStagerHooks? hooks, bool readOnlyExisting = false)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows installer staging is required.");
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _pin = ValidatePin(pin);
        _signaturePolicy = signaturePolicy;
        if (_pin.TrustMode == VelopackSetupTrustMode.Authenticode)
        {
            (_signaturePolicy ?? throw new InvalidDataException("Authenticode Setup pins require a signer policy.")).Validate();
        }
        else if (_pin.TrustMode == VelopackSetupTrustMode.SignedManifestHash && _signaturePolicy is not null)
        {
            throw new InvalidDataException("Certificate-free Setup pins cannot include an Authenticode signer policy.");
        }
        _initiatingUser = new SecurityIdentifier(initiatingUserSid ?? throw new ArgumentNullException(nameof(initiatingUserSid)));
        _readOnlyExisting = readOnlyExisting;
        if (readOnlyExisting)
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (!string.Equals(identity.User?.Value, _initiatingUser.Value, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("Only the initiating Windows user may open the staged Setup lease.");
        }
        else if (hooks is null) RequireElevatedAdministrator();
        _root = Path.GetFullPath(machineStagingRoot ?? throw new ArgumentNullException(nameof(machineStagingRoot)));
        var userPartition = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_initiatingUser.Value)))
            .ToLowerInvariant()[..24];
        _batchDirectory = Path.Combine(_root, "setup-" + _pin.Sha256.ToLowerInvariant() + "-" + userPartition);
        _handlePrefix = "windows-velopack-setup-v1:" + _pin.Sha256.ToLowerInvariant();
        _hooks = hooks ?? new VelopackSetupStagerHooks(
            PrepareProtectedDirectory, ValidateProtectedDirectory, ProtectFile, ValidateProtectedFile,
            VerifyAuthenticodeSigner);
    }

    public async ValueTask<IVerifiedVelopackSetupLease> StageAndVerifyAsync(CancellationToken cancellationToken = default)
    {
        if (_readOnlyExisting)
        {
            AssertNoReparseComponents(_batchDirectory);
            _hooks.ValidateProtectedDirectory(_root);
            _hooks.ValidateProtectedDirectory(_batchDirectory);
            return await TryOpenExistingLeaseAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("No verified Setup.exe slot was staged for the initiating user.");
        }
        AssertNoReparseComponents(_root);
        _hooks.PrepareProtectedDirectory(_root);
        AssertNoReparseComponents(_root);
        _hooks.PrepareProtectedDirectory(_batchDirectory);
        AssertNoReparseComponents(_batchDirectory);
        var sourcePath = NormalizeRegularFile(_source.SetupPath);
        await using var source = OpenReadPinned(sourcePath);
        AssertSamePath(source.SafeFileHandle, sourcePath);
        await InspectAsync(source, sourcePath, cancellationToken).ConfigureAwait(false);

        var existing = await TryOpenExistingLeaseAsync(cancellationToken).ConfigureAwait(false);
        if (existing is not null)
            return existing;

        var slot = CreateSlot();
        var destination = Path.Combine(slot.Path, "Setup.exe");
        var temporary = Path.Combine(slot.Path, ".Setup.exe." + Guid.NewGuid().ToString("N") + ".partial");
        FileStream? writer = null;
        FileStream? leaseStream = null;
        NtfsFileIdentity temporaryIdentity = default!;
        try
        {
            writer = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
            source.Position = 0;
            await source.CopyToAsync(writer, 128 * 1024, cancellationToken).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            writer.Flush(true);
            _hooks.ProtectFile(temporary);
            AssertNoReparseComponents(temporary);
            temporaryIdentity = GetNtfsIdentity(writer.SafeFileHandle);
            await writer.DisposeAsync().ConfigureAwait(false);
            writer = null;

            File.Move(temporary, destination, overwrite: false);
            _hooks.ValidateProtectedFile(destination);

            leaseStream = OpenReadPinned(destination);
            AssertSamePath(leaseStream.SafeFileHandle, destination);
            _hooks.ValidateProtectedFile(destination);
            var inspection = await InspectAsync(leaseStream, destination, cancellationToken).ConfigureAwait(false);
            var identity = GetNtfsIdentity(leaseStream.SafeFileHandle);
            if (identity != temporaryIdentity)
                throw new InvalidDataException("The published Setup.exe does not identify the protected temporary file.");
            var lease = new WindowsVerifiedVelopackSetupLease(HandleFor(slot.Id), destination, inspection, identity, leaseStream);
            leaseStream = null;
            return lease;
        }
        catch
        {
            if (writer is not null) await writer.DisposeAsync().ConfigureAwait(false);
            if (leaseStream is not null) await leaseStream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<IVerifiedVelopackSetupLease> ReacquireAsync(ProtectedVelopackSetupReceipt receipt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (!TryResolveReceiptDestination(receipt.HandleId, out var destination) ||
            !PathsEqual(receipt.StagedPath, destination) ||
            !MatchesPin(receipt.Inspection))
            throw new InvalidDataException("The durable Setup receipt does not belong to this staging operation.");
        _hooks.ValidateProtectedDirectory(_root);
        _hooks.ValidateProtectedDirectory(_batchDirectory);
        _hooks.ValidateProtectedDirectory(Path.GetDirectoryName(destination)!);
        _hooks.ValidateProtectedFile(destination);
        AssertNoReparseComponents(destination);
        var stream = OpenReadPinned(destination);
        try
        {
            AssertSamePath(stream.SafeFileHandle, destination);
            _hooks.ValidateProtectedFile(destination);
            var inspection = await InspectAsync(stream, destination, cancellationToken).ConfigureAwait(false);
            var identity = GetNtfsIdentity(stream.SafeFileHandle);
            if (inspection != receipt.Inspection || identity != receipt.FileIdentity)
                throw new InvalidDataException("The durable Setup payload drifted from its receipt.");
            return new WindowsVerifiedVelopackSetupLease(receipt.HandleId, destination, inspection, identity, stream);
        }
        catch { await stream.DisposeAsync().ConfigureAwait(false); throw; }
    }

    public async ValueTask<IVerifiedVelopackSetupLease> ReacquireByHandleAsync(string handleId,
        CancellationToken cancellationToken = default)
    {
        if (!TryResolveReceiptDestination(handleId, out var destination))
            throw new InvalidDataException("The durable Setup handle does not belong to this staging operation.");
        _hooks.ValidateProtectedDirectory(_root);
        _hooks.ValidateProtectedDirectory(_batchDirectory);
        _hooks.ValidateProtectedDirectory(Path.GetDirectoryName(destination)!);
        _hooks.ValidateProtectedFile(destination);
        AssertNoReparseComponents(destination);
        var stream = OpenReadPinned(destination);
        try
        {
            AssertSamePath(stream.SafeFileHandle, destination);
            _hooks.ValidateProtectedFile(destination);
            var inspection = await InspectAsync(stream, destination, cancellationToken).ConfigureAwait(false);
            return new WindowsVerifiedVelopackSetupLease(handleId, destination, inspection,
                GetNtfsIdentity(stream.SafeFileHandle), stream);
        }
        catch { await stream.DisposeAsync().ConfigureAwait(false); throw; }
    }

    private async ValueTask<VelopackSetupInspection> InspectAsync(FileStream stream, string path, CancellationToken token)
    {
        if (stream.Length != _pin.SizeBytes) throw new InvalidDataException("Setup.exe size differs from the pin.");
        stream.Position = 0;
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
        if (!string.Equals(hash, _pin.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Setup.exe SHA-256 differs from the pin.");
        string? thumbprint = null;
        if (_pin.TrustMode == VelopackSetupTrustMode.Authenticode)
        {
            thumbprint = NormalizeThumbprint(_hooks.VerifySigner(path));
            if (!_signaturePolicy!.AllowedSignerThumbprints.Select(NormalizeThumbprint).Contains(thumbprint, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("Setup.exe Authenticode signer is not allowed by the pinned policy.");
            if (!string.Equals(thumbprint, NormalizeThumbprint(_pin.SignerThumbprint), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Setup.exe Authenticode signer differs from the signed release pin.");
        }
        return new VelopackSetupInspection(_pin.PackId, _pin.Version, _pin.SizeBytes, hash, true, thumbprint, _pin.TrustMode);
    }

    private (Guid Id, string Path) CreateSlot()
    {
        for (var attempt = 0; attempt != 8; attempt++)
        {
            var id = Guid.NewGuid();
            var path = Path.Combine(_batchDirectory, SlotName(id));
            try
            {
                _hooks.PrepareProtectedDirectory(path);
                AssertNoReparseComponents(path);
                _hooks.ValidateProtectedDirectory(path);
                return (id, path);
            }
            catch (IOException) when (Directory.Exists(path))
            {
                // A concurrent stager chose the random slot. Never reuse an uninspected slot.
            }
        }
        throw new IOException("Could not reserve a protected Velopack Setup staging slot.");
    }

    private async ValueTask<IVerifiedVelopackSetupLease?> TryOpenExistingLeaseAsync(CancellationToken cancellationToken)
    {
        foreach (var slotPath in Directory.EnumerateDirectories(_batchDirectory, "stage-*", SearchOption.TopDirectoryOnly))
        {
            if (!TryParseSlotName(Path.GetFileName(slotPath), out var slotId))
                continue;
            AssertNoReparseComponents(slotPath);
            _hooks.ValidateProtectedDirectory(slotPath);
            var existing = await TryOpenVerifiedLeaseAsync(Path.Combine(slotPath, "Setup.exe"), HandleFor(slotId), cancellationToken).ConfigureAwait(false);
            if (existing is not null)
                return existing;
        }
        return null;
    }

    private async ValueTask<IVerifiedVelopackSetupLease?> TryOpenVerifiedLeaseAsync(string destination, string handleId,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(destination))
            return null;
        AssertNoReparseComponents(destination);
        _hooks.ValidateProtectedFile(destination);
        var stream = OpenReadPinned(destination);
        try
        {
            AssertSamePath(stream.SafeFileHandle, destination);
            _hooks.ValidateProtectedFile(destination);
            VelopackSetupInspection inspection;
            try
            {
                inspection = await InspectAsync(stream, destination, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidDataException)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                return null;
            }
            return new WindowsVerifiedVelopackSetupLease(handleId, destination, inspection,
                GetNtfsIdentity(stream.SafeFileHandle), stream);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private string HandleFor(Guid slotId) => _handlePrefix + ":stage:" + slotId.ToString("N");
    private static string SlotName(Guid slotId) => "stage-" + slotId.ToString("N");
    private bool TryResolveReceiptDestination(string handleId, out string destination)
    {
        var prefix = _handlePrefix + ":stage:";
        if (handleId.StartsWith(prefix, StringComparison.Ordinal) &&
            Guid.TryParseExact(handleId[prefix.Length..], "N", out var slotId))
        {
            destination = Path.Combine(_batchDirectory, SlotName(slotId), "Setup.exe");
            return true;
        }
        destination = string.Empty;
        return false;
    }
    private static bool TryParseSlotName(string value, out Guid slotId)
    {
        if (value.StartsWith("stage-", StringComparison.Ordinal))
            return Guid.TryParseExact(value[6..], "N", out slotId);
        slotId = Guid.Empty;
        return false;
    }

    private bool MatchesPin(VelopackSetupInspection inspection) =>
        inspection.SignatureVerified &&
        inspection.TrustMode == _pin.TrustMode &&
        (_pin.TrustMode == VelopackSetupTrustMode.SignedManifestHash
            ? inspection.SignatureEvidenceId is null
            : !string.IsNullOrWhiteSpace(inspection.SignatureEvidenceId)) &&
        string.Equals(inspection.PackId, _pin.PackId, StringComparison.Ordinal) &&
        string.Equals(inspection.Version, _pin.Version, StringComparison.Ordinal) &&
        inspection.SizeBytes == _pin.SizeBytes &&
        string.Equals(inspection.Sha256, _pin.Sha256, StringComparison.OrdinalIgnoreCase);

    private static VelopackSetupPin ValidatePin(VelopackSetupPin pin)
    {
        ArgumentNullException.ThrowIfNull(pin);
        if (string.IsNullOrWhiteSpace(pin.PackId) || string.IsNullOrWhiteSpace(pin.Version) || pin.SizeBytes <= 0 ||
            pin.Sha256.Length != 64 || !pin.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("The Setup pin is incomplete or invalid.");
        if (pin.TrustMode is not (VelopackSetupTrustMode.Authenticode or VelopackSetupTrustMode.SignedManifestHash))
            throw new InvalidDataException("The Setup pin trust mode is unsupported.");
        if (pin.TrustMode == VelopackSetupTrustMode.Authenticode && string.IsNullOrWhiteSpace(pin.SignerThumbprint))
            throw new InvalidDataException("The Authenticode Setup pin has no signer thumbprint.");
        if (pin.TrustMode == VelopackSetupTrustMode.SignedManifestHash && pin.SignerThumbprint is not null)
            throw new InvalidDataException("The certificate-free Setup pin cannot contain signer evidence.");
        return pin;
    }

    private static FileStream OpenReadPinned(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read,
        128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static string NormalizeRegularFile(string path)
    {
        var full = Path.GetFullPath(path ?? throw new ArgumentNullException(nameof(path)));
        var info = new FileInfo(full);
        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Setup source must be a regular existing file.");
        return full;
    }

    private void PrepareProtectedDirectory(string path)
    {
        var machineRoot = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(Path.TrimEndingDirectorySeparator(machineRoot) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Velopack staging must be inside ProgramData.");
        ValidateMachineAncestor(machineRoot);
        if (IsSharedDirectory(fullPath))
        {
            WindowsSharedMachineDirectoryAcl.PreparePath(machineRoot, fullPath);
            return;
        }
        AssertNoReparseComponents(path);
        var current = machineRoot;
        foreach (var component in Path.GetRelativePath(machineRoot, fullPath)
                     .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            AssertNoReparseComponents(current);
            if (Directory.Exists(current))
            {
                ValidateProtectedDirectory(current);
                continue;
            }
            if (File.Exists(current))
                throw new InvalidDataException("A Velopack staging path component is a file.");
            var security = CreateProtectedDirectoryAcl(IsSharedDirectory(current));
            FileSystemAclExtensions.Create(new DirectoryInfo(current), security);
            AssertNoReparseComponents(current);
            ValidateProtectedDirectory(current);
        }
    }

    private bool IsSharedDirectory(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var root = Path.TrimEndingDirectorySeparator(_root);
        return string.Equals(full, root, StringComparison.OrdinalIgnoreCase) ||
            root.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private DirectorySecurity CreateProtectedDirectoryAcl(bool sharedDirectory)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        // Shared ancestors must be traversable by every initiating user. Exact-SID access starts
        // at the per-user batch directory; neither level grants mutation rights to users.
        AddMachineRules(security, sharedDirectory
            ? new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null)
            : _initiatingUser);
        return security;
    }

    private static void ValidateMachineAncestor(string path)
    {
        AssertNoReparseComponents(path);
        var security = new DirectoryInfo(path).GetAccessControl(
            AccessControlSections.Owner | AccessControlSections.Access);
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value;
        var administratorsSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value;
        const string trustedInstallerSid =
            "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
        if (owner is null || owner.Value != systemSid && owner.Value != administratorsSid &&
            owner.Value != trustedInstallerSid)
            throw new UnauthorizedAccessException("ProgramData has an untrusted owner.");
        const FileSystemRights replacementRights =
            FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
            FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType == AccessControlType.Allow &&
                rule.IdentityReference.Value != systemSid &&
                rule.IdentityReference.Value != administratorsSid &&
                rule.IdentityReference.Value != trustedInstallerSid &&
                (rule.FileSystemRights & replacementRights) != 0)
                throw new UnauthorizedAccessException("ProgramData grants staging replacement rights to an untrusted principal.");
        }
    }

    private void ProtectFile(string path)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        AddMachineRules(security, _initiatingUser);
        new FileInfo(path).SetAccessControl(security);
        ValidateProtectedFile(path);
    }

    private static void AddMachineRules(FileSystemSecurity security, SecurityIdentifier initiatingUser)
    {
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(initiatingUser, FileSystemRights.ReadAndExecute, AccessControlType.Allow));
    }

    private void ValidateProtectedDirectory(string path) => ValidateProtectedAcl(
        new DirectoryInfo(path).GetAccessControl(), _initiatingUser, IsSharedDirectory(path));
    private void ValidateProtectedFile(string path) => ValidateProtectedAcl(new FileInfo(path).GetAccessControl(), _initiatingUser);
    internal static void ValidateProtectedAcl(FileSystemSecurity security, SecurityIdentifier initiatingUser,
        bool sharedDirectory = false)
    {
        if (sharedDirectory)
        {
            WindowsSharedMachineDirectoryAcl.Validate(security);
            return;
        }
        if (!security.AreAccessRulesProtected) throw new UnauthorizedAccessException("Machine staging ACL inherits access.");
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value,
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value };
        var reader = sharedDirectory
            ? new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null).Value
            : initiatingUser.Value;
        allowed.Add(reader);
        if (rules.Any(rule => rule.AccessControlType != AccessControlType.Allow ||
            !allowed.Contains(((SecurityIdentifier)rule.IdentityReference).Value)))
            throw new UnauthorizedAccessException("Machine staging ACL grants or denies an unexpected principal.");
        foreach (var machineSid in allowed.Take(2))
        {
            if (!rules.Any(rule => rule.AccessControlType == AccessControlType.Allow &&
                ((SecurityIdentifier)rule.IdentityReference).Value == machineSid &&
                (rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl))
                throw new UnauthorizedAccessException("Machine staging ACL lacks required SYSTEM or Administrators control.");
        }
        if (!rules.Any(rule => rule.AccessControlType == AccessControlType.Allow &&
            ((SecurityIdentifier)rule.IdentityReference).Value == reader &&
            (rule.FileSystemRights & FileSystemRights.ReadAndExecute) == FileSystemRights.ReadAndExecute))
            throw new UnauthorizedAccessException("The permitted user reader lacks read-and-execute access.");
        if (rules.Any(rule => ((SecurityIdentifier)rule.IdentityReference).Value == reader &&
            (rule.AccessControlType != AccessControlType.Allow || (rule.FileSystemRights & ~(FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize)) != 0)))
            throw new UnauthorizedAccessException("A staging reader may only read and execute Setup.exe.");
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner is null || owner != new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null))
            throw new UnauthorizedAccessException("Machine staging owner must be Builtin Administrators.");
    }

    private static void RequireElevatedAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("Machine Setup staging requires an elevated Administrators token; this component never self-elevates.");
    }

    internal static void AssertNoReparseComponents(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? throw new InvalidDataException("Staging path has no root.");
        var current = root;
        foreach (var component in full[root.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (string.IsNullOrEmpty(component)) continue;
            current = Path.Combine(current, component);
            if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Machine Setup staging rejects reparse-point path components.");
        }
    }

    private static void AssertSamePath(SafeFileHandle handle, string path)
    {
        using var reopened = OpenReadPinned(path);
        if (GetNtfsIdentity(handle) != GetNtfsIdentity(reopened.SafeFileHandle))
            throw new InvalidDataException("The reopened Setup path does not identify the pinned NTFS file.");
    }

    internal static NtfsFileIdentity GetNtfsIdentity(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return new NtfsFileIdentity(info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow);
    }

    private static bool PathsEqual(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
    private static string NormalizeThumbprint(string? value) => (value ?? string.Empty).Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();

    private static string VerifyAuthenticodeSigner(string path)
    {
        var fileInfo = new WinTrustFileInfo { cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(), pcwszFilePath = path };
        var filePointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, filePointer, false);
            var trustData = new WinTrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WinTrustData>(), dwUIChoice = 2, dwUnionChoice = 1,
                pFile = filePointer, dwProvFlags = 0x00000080,
            };
            var action = WinTrustActionGenericVerifyV2;
            if (WinVerifyTrust(IntPtr.Zero, ref action, ref trustData) != 0)
                throw new InvalidDataException("Setup.exe Authenticode verification failed.");
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
            if (!certificate.Verify()) throw new InvalidDataException("Setup.exe signer certificate chain is not trusted.");
            return certificate.Thumbprint ?? throw new InvalidDataException("Setup.exe signer has no thumbprint.");
        }
        finally { Marshal.FreeHGlobal(filePointer); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct ByHandleFileInformation
    { public uint FileAttributes; public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime; public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime; public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime; public uint VolumeSerialNumber; public uint FileSizeHigh; public uint FileSizeLow; public uint NumberOfLinks; public uint FileIndexHigh; public uint FileIndexLow; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);
    private static readonly Guid WinTrustActionGenericVerifyV2 = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WinTrustFileInfo { public uint cbStruct; public string? pcwszFilePath; public IntPtr hFile; public IntPtr pgKnownSubject; }
    [StructLayout(LayoutKind.Sequential)] private struct WinTrustData { public uint cbStruct; public IntPtr pPolicyCallbackData; public IntPtr pSIPClientData; public uint dwUIChoice; public uint fdwRevocationChecks; public uint dwUnionChoice; public IntPtr pFile; public uint dwStateAction; public IntPtr hWVTStateData; public IntPtr pwszURLReference; public uint dwProvFlags; public uint dwUIContext; public IntPtr pSignatureSettings; }
    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true)] private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid actionId, ref WinTrustData data);
}

internal sealed record VelopackSetupStagerHooks(
    Action<string> PrepareProtectedDirectory,
    Action<string> ValidateProtectedDirectory,
    Action<string> ProtectFile,
    Action<string> ValidateProtectedFile,
    Func<string, string> VerifySigner);

internal sealed class WindowsVerifiedVelopackSetupLease(string handleId, string stagedPath,
    VelopackSetupInspection inspection, NtfsFileIdentity fileIdentity, FileStream stream) : IWindowsVerifiedVelopackSetupLease
{
    private FileStream? _stream = stream;
    public string HandleId { get; } = handleId;
    public string StagedPath { get; } = stagedPath;
    public VelopackSetupLeaseProtection Protection => VelopackSetupLeaseProtection.ProtectedInstallerStaging;
    public VelopackSetupInspection Inspection { get; } = inspection;
    public NtfsFileIdentity FileIdentity { get; } = fileIdentity;
    public SafeFileHandle ContentHandle => _stream?.SafeFileHandle ?? throw new ObjectDisposedException(nameof(WindowsVerifiedVelopackSetupLease));
    public void VerifyLaunchPath()
    {
        if (_stream is null) throw new ObjectDisposedException(nameof(WindowsVerifiedVelopackSetupLease));
        using var reopened = new FileStream(StagedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (WindowsVelopackSetupStager.GetNtfsIdentity(reopened.SafeFileHandle) != FileIdentity ||
            WindowsVelopackSetupStager.GetNtfsIdentity(ContentHandle) != FileIdentity)
            throw new InvalidDataException("Setup launch path no longer identifies the leased NTFS file.");
    }
    public async ValueTask DisposeAsync() { var value = Interlocked.Exchange(ref _stream, null); if (value is not null) await value.DisposeAsync().ConfigureAwait(false); }
}
