using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Connector.Upgrade.Core;

namespace Connector.Upgrade.WindowsJournal;

/// <summary>
/// Machine-protected, atomic journal store. The containing directory must already be created with
/// a protected machine ACL; every managed file is independently protected and revalidated.
/// </summary>
public sealed class WindowsProtectedUpgradeJournalStore : IUpgradeJournalStore, IDisposable
{
    private const long MaximumJournalBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _rootPath;
    private readonly string _journalPath;
    private readonly string _leasePath;
    private readonly WindowsJournalSecurityProfile _securityProfile;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _leaseHeld;
    private int _disposeRequested;

    public WindowsProtectedUpgradeJournalStore(string protectedDirectory)
        : this(protectedDirectory, WindowsJournalSecurity.ProductionProfile)
    {
    }

    internal WindowsProtectedUpgradeJournalStore(
        string protectedDirectory,
        WindowsJournalSecurityProfile securityProfile)
    {
        _securityProfile = securityProfile ?? throw new ArgumentNullException(nameof(securityProfile));
        WindowsJournalSecurity.RequireTrustedElevatedCaller(_securityProfile);
        _rootPath = Path.GetFullPath(
            string.IsNullOrWhiteSpace(protectedDirectory)
                ? throw new ArgumentException("Protected journal directory is required.", nameof(protectedDirectory))
                : protectedDirectory);
        _journalPath = Path.Combine(_rootPath, "journal.json");
        _leasePath = Path.Combine(_rootPath, "journal.lease");
        ValidateRoot();
    }

    public async ValueTask<IUpgradeJournalLease> AcquireLeaseAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (Volatile.Read(ref _leaseHeld) != 0)
                throw new UpgradeLeaseUnavailableException("This journal store already owns an active lease.");
            ValidateRoot();
            var stream = OpenOrCreateLease();
            WindowsJournalSecurity.ValidateProtectedFile(_leasePath, _securityProfile);
            ValidateRoot();
            Volatile.Write(ref _leaseHeld, 1);
            return new ProtectedJournalLease(this, stream);
        }
        catch (IOException error)
        {
            throw new UpgradeLeaseUnavailableException("Another upgrade process holds the protected journal lease.", error);
        }
        catch (UnauthorizedAccessException error)
        {
            throw new UpgradeLeaseUnavailableException("The protected journal lease cannot be acquired.", error);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<UpgradeJournalDocument?> LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            RequireOwnedLease();
            ValidateRoot();
            return await ReadUnlockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask SaveAsync(UpgradeJournalDocument journal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(journal);
        if (journal.SchemaVersion != UpgradeJournalDocument.CurrentSchemaVersion)
            throw new InvalidDataException($"Unsupported journal schema {journal.SchemaVersion}.");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporaryPath = null;
        try
        {
            ThrowIfDisposed();
            RequireOwnedLease();
            ValidateRoot();
            var current = await ReadUnlockedAsync(cancellationToken).ConfigureAwait(false);
            if (current is null)
            {
                if (journal.Revision != 0)
                    throw new InvalidOperationException("The first journal revision must be zero.");
            }
            else if (current.RunId != journal.RunId || journal.Revision != current.Revision + 1)
            {
                throw new InvalidOperationException("Journal compare-and-swap revision mismatch.");
            }

            temporaryPath = Path.Combine(_rootPath, $".journal.{Guid.NewGuid():N}.tmp");
            await using (var stream = WindowsJournalSecurity.CreateProtectedFile(
                             temporaryPath,
                             FileAccess.Write,
                             FileShare.None,
                             FileOptions.Asynchronous | FileOptions.WriteThrough,
                             _securityProfile))
            {
                await JsonSerializer.SerializeAsync(stream, journal, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            WindowsJournalSecurity.ValidateProtectedFile(temporaryPath, _securityProfile);
            ValidateRoot();
            if (File.Exists(_journalPath))
                WindowsJournalSecurity.ValidateProtectedFile(_journalPath, _securityProfile);
            ReplaceFileDurably(temporaryPath, _journalPath);
            temporaryPath = null;
            WindowsJournalSecurity.ValidateProtectedFile(_journalPath, _securityProfile);
            ValidateRoot();
        }
        finally
        {
            if (temporaryPath is not null && File.Exists(temporaryPath))
                File.Delete(temporaryPath);
            _gate.Release();
        }
    }

    public void Dispose() => Interlocked.Exchange(ref _disposeRequested, 1);

    private FileStream OpenOrCreateLease()
    {
        if (File.Exists(_leasePath))
        {
            WindowsJournalSecurity.ValidateProtectedFile(_leasePath, _securityProfile);
            return OpenExistingLease();
        }

        try
        {
            return WindowsJournalSecurity.CreateProtectedFile(
                _leasePath,
                FileAccess.ReadWrite,
                FileShare.None,
                FileOptions.WriteThrough,
                _securityProfile);
        }
        catch (IOException) when (File.Exists(_leasePath))
        {
            WindowsJournalSecurity.ValidateProtectedFile(_leasePath, _securityProfile);
            return OpenExistingLease();
        }
    }

    private FileStream OpenExistingLease() => new(
        _leasePath,
        FileMode.Open,
        FileAccess.ReadWrite,
        FileShare.None,
        1,
        FileOptions.WriteThrough);

    private async ValueTask<UpgradeJournalDocument?> ReadUnlockedAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_journalPath))
            return null;

        WindowsJournalSecurity.ValidateProtectedFile(_journalPath, _securityProfile);
        await using var stream = new FileStream(
            _journalPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        WindowsJournalSecurity.ValidateProtectedFile(_journalPath, _securityProfile);
        if (stream.Length > MaximumJournalBytes)
            throw new InvalidDataException("The protected upgrade journal exceeds its size limit.");
        var journal = await JsonSerializer.DeserializeAsync<UpgradeJournalDocument>(
                stream,
                JsonOptions,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException("The protected upgrade journal is empty.");
        if (journal.SchemaVersion != UpgradeJournalDocument.CurrentSchemaVersion)
            throw new InvalidDataException($"Unsupported journal schema {journal.SchemaVersion}.");
        return journal;
    }

    private void ValidateRoot()
    {
        if (!Directory.Exists(_rootPath) || File.Exists(_rootPath))
            throw new DirectoryNotFoundException("The protected upgrade journal directory is missing.");
        WindowsJournalSecurity.ValidateProtectedDirectory(_rootPath, _securityProfile);
    }

    private void RequireOwnedLease()
    {
        if (Volatile.Read(ref _leaseHeld) == 0)
            throw new InvalidOperationException(
                "Load and save require an active lease acquired from this protected journal store.");
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposeRequested) != 0)
            throw new ObjectDisposedException(nameof(WindowsProtectedUpgradeJournalStore));
    }

    private async ValueTask ReleaseLeaseAsync(FileStream stream)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            stream.Dispose();
            Volatile.Write(ref _leaseHeld, 0);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void ReplaceFileDurably(string sourcePath, string destinationPath)
    {
        const MoveFileFlags flags = MoveFileFlags.ReplaceExisting | MoveFileFlags.WriteThrough;
        if (!MoveFileEx(sourcePath, destinationPath, flags))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The protected upgrade journal could not be committed atomically.");
    }

    [Flags]
    private enum MoveFileFlags : uint
    {
        ReplaceExisting = 0x1,
        WriteThrough = 0x8,
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(
        string existingFileName,
        string newFileName,
        MoveFileFlags flags);

    private sealed class ProtectedJournalLease(
        WindowsProtectedUpgradeJournalStore owner,
        FileStream stream) : IUpgradeJournalLease
    {
        private int _disposed;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                await owner.ReleaseLeaseAsync(stream).ConfigureAwait(false);
        }
    }
}
