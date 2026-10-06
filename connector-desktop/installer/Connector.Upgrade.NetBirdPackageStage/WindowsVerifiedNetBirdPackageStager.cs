using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using Connector.Upgrade.Core;
using Connector.Upgrade.NetBirdMachine;
using Microsoft.Win32.SafeHandles;

namespace Connector.Upgrade.NetBirdPackageStage;

public sealed class WindowsVerifiedNetBirdPackageStager : IVerifiedNetBirdPackageStager
{
    private readonly string _root;
    private readonly INetBirdMachineStagingSecurity _security;
    private readonly INetBirdMsiPropertyReader _msi;
    private readonly IAuthenticodeSignerVerifier _signer;
    private readonly bool _requireEmbeddedPin;

    public WindowsVerifiedNetBirdPackageStager(string machineStagingRoot)
        : this(machineStagingRoot, new WindowsNetBirdMachineStagingSecurity(), new WindowsNetBirdMsiPropertyReader(), new WindowsAuthenticodeSignerVerifier(), requireEmbeddedPin: true)
    {
    }

    internal WindowsVerifiedNetBirdPackageStager(string machineStagingRoot, INetBirdMachineStagingSecurity security,
        INetBirdMsiPropertyReader msi, IAuthenticodeSignerVerifier signer, bool requireEmbeddedPin = false)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("NetBird protected MSI staging is available only on Windows.");
        _security = security ?? throw new ArgumentNullException(nameof(security));
        _msi = msi ?? throw new ArgumentNullException(nameof(msi));
        _signer = signer ?? throw new ArgumentNullException(nameof(signer));
        _requireEmbeddedPin = requireEmbeddedPin;
        _root = _security.PrepareRoot(machineStagingRoot ?? throw new ArgumentNullException(nameof(machineStagingRoot)));
        _security.ValidateProtectedDirectory(_root);
    }

    public async ValueTask<IVerifiedNetBirdPackageLease> StageAndVerifyAsync(string sourceMsiPath,
        OfficialNetBirdPackagePin pin, CancellationToken cancellationToken)
    {
        ValidatePin(pin, _requireEmbeddedPin);
        var sourcePath = NormalizeRegularFile(sourceMsiPath);
        if (!string.Equals(Path.GetFileName(sourcePath), pin.InstallerName, StringComparison.Ordinal))
            throw new InvalidDataException("NetBird MSI source name differs from the official lock.");

        await using var source = OpenPinnedRead(sourcePath);
        WindowsNetBirdPathSafety.AssertHandleMatchesPath(source.SafeFileHandle, sourcePath);
        await InspectAsync(source, sourcePath, pin, cancellationToken).ConfigureAwait(false);

        var directory = GetContentDirectory(pin);
        EnsureContentDirectory(directory);
        var existing = await TryOpenExistingLeaseAsync(directory, pin, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
            return existing;

        var slot = CreateSlot(directory);
        var destination = Path.Combine(slot.Path, pin.InstallerName);
        var temporary = Path.Combine(slot.Path, "." + pin.InstallerName + "." + Guid.NewGuid().ToString("N") + ".partial");
        FileStream? writer = null;
        FileStream? leaseStream = null;
        try
        {
            writer = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
            source.Position = 0;
            await source.CopyToAsync(writer, 128 * 1024, cancellationToken).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            writer.Flush(flushToDisk: true);
            _security.ProtectAndValidateFile(temporary);
            WindowsNetBirdPathSafety.AssertHandleMatchesPath(writer.SafeFileHandle, temporary);
            await writer.DisposeAsync().ConfigureAwait(false);
            writer = null;

            File.Move(temporary, destination, overwrite: false);
            _security.ValidateProtectedFile(destination);

            leaseStream = OpenPinnedRead(destination);
            WindowsNetBirdPathSafety.AssertHandleMatchesPath(leaseStream.SafeFileHandle, destination);
            _security.ValidateProtectedFile(destination);
            var inspected = await InspectAsync(leaseStream, destination, pin, cancellationToken).ConfigureAwait(false);
            var lease = new WindowsVerifiedNetBirdPackageLease(HandleFor(pin, slot.Id), destination, inspected, leaseStream);
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

    public async ValueTask<IVerifiedNetBirdPackageLease> ReacquireAsync(ProtectedNetBirdPackageReceipt receipt,
        OfficialNetBirdPackagePin pin, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ValidatePin(pin, _requireEmbeddedPin);
        var directory = GetContentDirectory(pin);
        if (!TryResolveReceiptDestination(receipt.HandleId, pin, directory, out var destination) ||
            !ReceiptBelongsToPin(receipt, pin, _requireEmbeddedPin))
            throw new InvalidDataException("The NetBird package receipt does not belong to this official lock.");

        _security.ValidateProtectedDirectory(Path.GetDirectoryName(destination)!);
        _security.ValidateProtectedFile(destination);
        var stream = OpenPinnedRead(destination);
        try
        {
            WindowsNetBirdPathSafety.AssertHandleMatchesPath(stream.SafeFileHandle, destination);
            _security.ValidateProtectedFile(destination);
            var inspected = await InspectAsync(stream, destination, pin, cancellationToken).ConfigureAwait(false);
            if (!MatchesReceipt(inspected, receipt))
                throw new InvalidDataException("The protected NetBird package drifted from its durable receipt.");
            return new WindowsVerifiedNetBirdPackageLease(receipt.HandleId, destination, inspected, stream);
        }
        catch { await stream.DisposeAsync().ConfigureAwait(false); throw; }
    }

    private void EnsureContentDirectory(string directory)
    {
        if (!Directory.Exists(directory))
            _security.CreateProtectedDirectory(directory);
        _security.ValidateProtectedDirectory(directory);
    }

    private (Guid Id, string Path) CreateSlot(string directory)
    {
        for (var attempt = 0; attempt != 8; attempt++)
        {
            var id = Guid.NewGuid();
            var path = Path.Combine(directory, SlotName(id));
            try
            {
                _security.CreateProtectedDirectory(path);
                _security.ValidateProtectedDirectory(path);
                return (id, path);
            }
            catch (IOException) when (Directory.Exists(path))
            {
                // A concurrent stager chose the same random slot. Never reuse it without inspection.
            }
        }
        throw new IOException("Could not reserve a protected NetBird package staging slot.");
    }

    private async ValueTask<IVerifiedNetBirdPackageLease?> TryOpenExistingLeaseAsync(string directory,
        OfficialNetBirdPackagePin pin, CancellationToken cancellationToken)
    {
        var legacy = Path.Combine(directory, pin.InstallerName);
        var existing = await TryOpenVerifiedLeaseAsync(legacy, HandleFor(pin), pin, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
            return existing;

        foreach (var slotPath in Directory.EnumerateDirectories(directory, "stage-*", SearchOption.TopDirectoryOnly))
        {
            var slotName = Path.GetFileName(slotPath);
            if (!TryParseSlotName(slotName, out var slotId))
                continue;
            _security.ValidateProtectedDirectory(slotPath);
            existing = await TryOpenVerifiedLeaseAsync(Path.Combine(slotPath, pin.InstallerName), HandleFor(pin, slotId), pin, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
                return existing;
        }
        return null;
    }

    private async ValueTask<IVerifiedNetBirdPackageLease?> TryOpenVerifiedLeaseAsync(string destination,
        string handleId, OfficialNetBirdPackagePin pin, CancellationToken cancellationToken)
    {
        if (!File.Exists(destination))
            return null;
        _security.ValidateProtectedFile(destination);
        var stream = OpenPinnedRead(destination);
        try
        {
            WindowsNetBirdPathSafety.AssertHandleMatchesPath(stream.SafeFileHandle, destination);
            _security.ValidateProtectedFile(destination);
            var inspected = await InspectAsync(stream, destination, pin, cancellationToken).ConfigureAwait(false);
            return new WindowsVerifiedNetBirdPackageLease(handleId, destination, inspected, stream);
        }
        catch (InvalidDataException)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            return null;
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask<VerifiedNetBirdPackageInspection> InspectAsync(FileStream stream, string path,
        OfficialNetBirdPackagePin pin, CancellationToken cancellationToken)
    {
        if (stream.Length != pin.InstallerBytes)
            throw new InvalidDataException("NetBird MSI size differs from the official lock.");
        stream.Position = 0;
        var sha = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        if (!string.Equals(sha, pin.InstallerSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("NetBird MSI SHA-256 differs from the official lock.");

        var properties = _msi.Read(path);
        if (properties.ProductCode == Guid.Empty || properties.UpgradeCode == Guid.Empty ||
            (_requireEmbeddedPin && (properties.ProductCode != ExpectedProductCode || properties.UpgradeCode != ExpectedUpgradeCode)) ||
            !string.Equals(properties.ProductVersion, pin.Version, StringComparison.Ordinal))
            throw new InvalidDataException("NetBird MSI identity differs from the official lock.");

        var signer = _signer.VerifyTrusted(path);
        if (!signer.Subject.StartsWith(pin.SignerSubjectPrefix, StringComparison.Ordinal) ||
            !string.Equals(NormalizeThumbprint(signer.Thumbprint), NormalizeThumbprint(pin.SignerThumbprint), StringComparison.Ordinal))
            throw new InvalidDataException("NetBird MSI signer differs from the official lock.");

        return new VerifiedNetBirdPackageInspection(
            new NetBirdMsiPackageIdentity(properties.ProductCode, properties.UpgradeCode, properties.ProductVersion,
                properties.Manufacturer, properties.ProductName), stream.Length, sha, signer.Subject, NormalizeThumbprint(signer.Thumbprint));
    }

    private string GetContentDirectory(OfficialNetBirdPackagePin pin) =>
        Path.Combine(_root, "sha256-" + pin.InstallerSha256.ToLowerInvariant());
    private static string HandleFor(OfficialNetBirdPackagePin pin, Guid? slotId = null) =>
        "netbird-msi-v1:sha256:" + pin.InstallerSha256.ToLowerInvariant() +
        (slotId is null ? string.Empty : ":stage:" + slotId.Value.ToString("N"));
    private static string SlotName(Guid id) => "stage-" + id.ToString("N");
    private static bool TryParseSlotName(string value, out Guid id)
    {
        if (value.StartsWith("stage-", StringComparison.Ordinal))
            return Guid.TryParseExact(value[6..], "N", out id);
        id = Guid.Empty;
        return false;
    }
    private static bool TryResolveReceiptDestination(string handleId, OfficialNetBirdPackagePin pin,
        string directory, out string destination)
    {
        if (string.Equals(handleId, HandleFor(pin), StringComparison.Ordinal))
        {
            destination = Path.Combine(directory, pin.InstallerName);
            return true;
        }

        var prefix = HandleFor(pin) + ":stage:";
        if (handleId.StartsWith(prefix, StringComparison.Ordinal) &&
            Guid.TryParseExact(handleId[prefix.Length..], "N", out var slotId))
        {
            destination = Path.Combine(directory, SlotName(slotId), pin.InstallerName);
            return true;
        }

        destination = string.Empty;
        return false;
    }
    private static readonly Guid ExpectedProductCode = Guid.Parse("463D0C9D-ED41-451E-A44F-937932C8B267");
    private static readonly Guid ExpectedUpgradeCode = Guid.Parse("6456EC4E-3AD6-4B9B-A2BE-98E81CB21CCF");
    private static void ValidatePin(OfficialNetBirdPackagePin pin, bool requireEmbeddedPin)
    {
        ArgumentNullException.ThrowIfNull(pin);
        var embedded = OfficialNetBirdPackagePin.LoadEmbedded();
        if ((requireEmbeddedPin && pin != embedded) || pin.InstallerSha256.Length != 64 || !pin.InstallerSha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("NetBird staging accepts only the complete embedded official lock.");
    }
    private static bool ReceiptBelongsToPin(ProtectedNetBirdPackageReceipt receipt, OfficialNetBirdPackagePin pin, bool requireExpectedMsiIdentity) =>
        receipt.Protection == RollbackPayloadProtection.ProtectedMachineStaging &&
        string.Equals(receipt.InstallerName, pin.InstallerName, StringComparison.Ordinal) &&
        receipt.SizeBytes == pin.InstallerBytes && string.Equals(receipt.Sha256, pin.InstallerSha256, StringComparison.OrdinalIgnoreCase) &&
        (!requireExpectedMsiIdentity || (receipt.Package.ProductCode == ExpectedProductCode && receipt.Package.UpgradeCode == ExpectedUpgradeCode)) &&
        string.Equals(receipt.Package.ProductVersion, pin.Version, StringComparison.Ordinal) &&
        receipt.SignerSubject.StartsWith(pin.SignerSubjectPrefix, StringComparison.Ordinal) &&
        string.Equals(NormalizeThumbprint(receipt.SignerThumbprint), NormalizeThumbprint(pin.SignerThumbprint), StringComparison.Ordinal);
    private static bool MatchesReceipt(VerifiedNetBirdPackageInspection value, ProtectedNetBirdPackageReceipt receipt) =>
        value.Package == receipt.Package && value.SizeBytes == receipt.SizeBytes &&
        string.Equals(value.Sha256, receipt.Sha256, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(value.SignerSubject, receipt.SignerSubject, StringComparison.Ordinal) &&
        string.Equals(value.SignerThumbprint, NormalizeThumbprint(receipt.SignerThumbprint), StringComparison.Ordinal);
    private static string NormalizeRegularFile(string path)
    {
        var full = Path.GetFullPath(path ?? throw new ArgumentNullException(nameof(path)));
        WindowsNetBirdPathSafety.AssertNoReparseComponents(full);
        var attributes = File.GetAttributes(full);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new InvalidDataException("NetBird source must be a regular non-reparse MSI file.");
        return full;
    }
    private static FileStream OpenPinnedRead(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read,
        128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
    private static string NormalizeThumbprint(string? value) => (value ?? string.Empty).Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
}

internal sealed class WindowsVerifiedNetBirdPackageLease(string handleId, string path,
    VerifiedNetBirdPackageInspection inspection, FileStream stream) : IVerifiedNetBirdPackageLease
{
    private FileStream? _stream = stream;
    public string HandleId { get; } = handleId;
    public string StagedPath { get; } = path;
    public RollbackPayloadProtection Protection => RollbackPayloadProtection.ProtectedMachineStaging;
    public NetBirdMsiPackageIdentity Package { get; } = inspection.Package;
    public long SizeBytes { get; } = inspection.SizeBytes;
    public string Sha256 { get; } = inspection.Sha256;
    public bool AuthenticodeTrusted => true;
    public string SignerSubject { get; } = inspection.SignerSubject;
    public string SignerThumbprint { get; } = inspection.SignerThumbprint;
    public async ValueTask DisposeAsync() { var value = Interlocked.Exchange(ref _stream, null); if (value is not null) await value.DisposeAsync().ConfigureAwait(false); }
}

internal sealed class WindowsNetBirdMsiPropertyReader : INetBirdMsiPropertyReader
{
    public NetBirdMsiProperties Read(string path)
    {
        var database = 0u;
        Throw(MsiOpenDatabase(path, IntPtr.Zero, out database), "Could not open NetBird MSI.");
        try { return new NetBirdMsiProperties(ParseGuid(Read(database, "ProductCode")), ParseGuid(Read(database, "UpgradeCode")), Read(database, "ProductVersion"), Read(database, "Manufacturer"), Read(database, "ProductName")); }
        finally { MsiCloseHandle(database); }
    }
    private static Guid ParseGuid(string value) => Guid.TryParse(value, out var result) && result != Guid.Empty ? result : throw new InvalidDataException("NetBird MSI identity is invalid.");
    private static string Read(uint database, string property)
    {
        Throw(MsiDatabaseOpenView(database, $"SELECT `Value` FROM `Property` WHERE `Property` = '{property}'", out var view), "Could not query NetBird MSI.");
        try { Throw(MsiViewExecute(view, 0), "Could not execute NetBird MSI query."); Throw(MsiViewFetch(view, out var record), "NetBird MSI property is missing."); try { uint length = 0; var measure = MsiRecordGetString(record, 1, null, ref length); if (measure is not 0 and not 234) Throw(measure, "Could not measure NetBird MSI property."); var text = new StringBuilder(checked((int)length + 1)); var capacity = (uint)text.Capacity; Throw(MsiRecordGetString(record, 1, text, ref capacity), "Could not read NetBird MSI property."); return text.ToString(); } finally { MsiCloseHandle(record); } } finally { MsiCloseHandle(view); }
    }
    private static void Throw(uint code, string message) { if (code != 0) throw new Win32Exception(unchecked((int)code), message); }
    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiOpenDatabaseW")] private static extern uint MsiOpenDatabase(string path, IntPtr persist, out uint db);
    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiDatabaseOpenViewW")] private static extern uint MsiDatabaseOpenView(uint db, string query, out uint view);
    [DllImport("msi.dll")] private static extern uint MsiViewExecute(uint view, uint record);
    [DllImport("msi.dll")] private static extern uint MsiViewFetch(uint view, out uint record);
    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiRecordGetStringW")] private static extern uint MsiRecordGetString(uint record, uint field, StringBuilder? value, ref uint length);
    [DllImport("msi.dll")] private static extern uint MsiCloseHandle(uint handle);
}

internal sealed class WindowsAuthenticodeSignerVerifier : IAuthenticodeSignerVerifier
{
    public AuthenticodeSigner VerifyTrusted(string path)
    {
        var info = new WinTrustFileInfo { cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(), pcwszFilePath = path };
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try { Marshal.StructureToPtr(info, pointer, false); var data = new WinTrustData { cbStruct = (uint)Marshal.SizeOf<WinTrustData>(), dwUIChoice = 2, dwUnionChoice = 1, pFile = pointer, dwProvFlags = 0x80 }; var action = Action; if (WinVerifyTrust(IntPtr.Zero, ref action, ref data) != 0) throw new InvalidDataException("NetBird MSI Authenticode verification failed."); using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path)); if (!certificate.Verify() || string.IsNullOrWhiteSpace(certificate.Subject) || string.IsNullOrWhiteSpace(certificate.Thumbprint)) throw new InvalidDataException("NetBird MSI signer certificate is not trusted."); return new AuthenticodeSigner(certificate.Subject, certificate.Thumbprint); } finally { Marshal.FreeHGlobal(pointer); }
    }
    private static readonly Guid Action = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WinTrustFileInfo { public uint cbStruct; public string? pcwszFilePath; public IntPtr hFile; public IntPtr pgKnownSubject; }
    [StructLayout(LayoutKind.Sequential)] private struct WinTrustData { public uint cbStruct; public IntPtr pPolicyCallbackData; public IntPtr pSIPClientData; public uint dwUIChoice; public uint fdwRevocationChecks; public uint dwUnionChoice; public IntPtr pFile; public uint dwStateAction; public IntPtr hWVTStateData; public IntPtr pwszURLReference; public uint dwProvFlags; public uint dwUIContext; public IntPtr pSignatureSettings; }
    [DllImport("wintrust.dll", ExactSpelling = true)] private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WinTrustData data);
}
