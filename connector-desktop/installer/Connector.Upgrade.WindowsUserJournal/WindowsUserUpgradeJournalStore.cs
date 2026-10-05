using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using Connector.Upgrade.Core;

namespace Connector.Upgrade.WindowsUserJournal;

/// <summary>A journal owned by the original interactive user, usable before UAC elevation.</summary>
public sealed class WindowsUserUpgradeJournalStore : IUpgradeJournalStore, IDisposable
{
    private const long MaximumJournalBytes = 16 * 1024 * 1024;
    private static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier AdministratorsSid = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _rootPath;
    private readonly string _rootBoundary;
    private readonly string _journalPath;
    private readonly string _leasePath;
    private readonly string _bootstrapperGuardPath;
    private readonly string _rolloverPath;
    private readonly SecurityIdentifier _userSid;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Action<Stream, UpgradeJournalDocument>? _writer;
    private readonly Action<string>? _rolloverCheckpoint;
    private int _leaseHeld;
    private int _disposed;

    /// <param name="originalUserSid">SID captured by the unelevated orchestrator before UAC.</param>
    public WindowsUserUpgradeJournalStore(string originalUserSid)
        : this(GetDefaultRoot(), originalUserSid, allowNonLocalAppDataRoot: false, writer: null)
    {
    }

    internal WindowsUserUpgradeJournalStore(
        string rootPath,
        string originalUserSid,
        bool allowNonLocalAppDataRoot,
        Action<Stream, UpgradeJournalDocument>? writer = null,
        string? rootBoundary = null,
        Action<string>? rolloverCheckpoint = null)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The original-user upgrade journal requires Windows.");
        if (string.IsNullOrWhiteSpace(originalUserSid))
            throw new ArgumentException("The original user SID is required.", nameof(originalUserSid));

        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        var actualSid = identity.User
            ?? throw new UnauthorizedAccessException("The current Windows identity has no user SID.");
        _userSid = new SecurityIdentifier(originalUserSid);
        if (!StringComparer.Ordinal.Equals(actualSid.Value, _userSid.Value))
            throw new UnauthorizedAccessException("The upgrade journal SID does not match the current Windows user.");

        _rootPath = Path.GetFullPath(string.IsNullOrWhiteSpace(rootPath)
            ? throw new ArgumentException("The journal root is required.", nameof(rootPath))
            : rootPath);
        if (!allowNonLocalAppDataRoot && !IsUnderLocalAppData(_rootPath))
            throw new UnauthorizedAccessException("The original-user journal must be under LocalAppData.");
        var boundary = allowNonLocalAppDataRoot
            ? rootBoundary ?? Path.GetDirectoryName(_rootPath)!
            : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _rootBoundary = Path.GetFullPath(boundary);
        if (!IsStrictlyUnder(_rootBoundary, _rootPath))
            throw new UnauthorizedAccessException("The original-user journal must be below its trusted directory boundary.");
        _journalPath = Path.Combine(_rootPath, "journal.json");
        _leasePath = Path.Combine(_rootPath, "journal.lease");
        _bootstrapperGuardPath = Path.Combine(_rootPath, "bootstrapper.guard");
        _rolloverPath = Path.Combine(_rootPath, "rollover.json");
        _writer = writer;
        _rolloverCheckpoint = rolloverCheckpoint;
        EnsureRoot();
    }

    public async ValueTask<IUpgradeJournalLease> AcquireLeaseAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (Volatile.Read(ref _leaseHeld) != 0)
                throw new UpgradeLeaseUnavailableException("This original-user journal already owns a lease.");
            ValidateRoot();
            FileStream stream;
            try
            {
                stream = OpenLease();
            }
            catch (IOException error)
            {
                throw new UpgradeLeaseUnavailableException("Another upgrade process holds the original-user journal lease.", error);
            }
            catch (UnauthorizedAccessException error)
            {
                throw new UpgradeLeaseUnavailableException("The original-user journal lease cannot be acquired.", error);
            }

            ValidateFile(_leasePath);
            ValidateRoot();
            Volatile.Write(ref _leaseHeld, 1);
            return new UserJournalLease(this, stream);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Excludes a second bootstrapper from prompting while the first owns the original-user upgrade UI.</summary>
    public IDisposable AcquireBootstrapperRunGuard()
    {
        ThrowIfDisposed();
        ValidateRoot();
        FileStream stream;
        try { stream = OpenProtectedExclusive(_bootstrapperGuardPath); }
        catch (IOException error)
        {
            throw new UpgradeLeaseUnavailableException("Another bootstrapper already owns this user's upgrade UI.", error);
        }
        catch (UnauthorizedAccessException error)
        {
            throw new UpgradeLeaseUnavailableException("The original-user bootstrapper guard cannot be acquired.", error);
        }
        try
        {
            ValidateFile(_bootstrapperGuardPath);
            ValidateRoot();
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public async ValueTask<UpgradeJournalDocument?> LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            RequireLease();
            ValidateRoot();
            return await ReadUnlockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
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
            RequireLease();
            ValidateRoot();
            var current = await ReadUnlockedAsync(cancellationToken).ConfigureAwait(false);
            if (current?.PreserveOriginalBytes == true)
                throw new InvalidDataException("The legacy original-user journal is preserved for manual recovery or terminal reporting.");
            if (current is null)
            {
                if (journal.Revision != 0) throw new InvalidOperationException("The first journal revision must be zero.");
            }
            else if (current.RunId != journal.RunId || journal.Revision != current.Revision + 1)
                throw new InvalidOperationException("Journal compare-and-swap revision mismatch.");

            temporaryPath = Path.Combine(_rootPath, $".journal.{Guid.NewGuid():N}.tmp");
            await using (var stream = FileSystemAclExtensions.Create(new FileInfo(temporaryPath), FileMode.CreateNew,
                             FileSystemRights.Write, FileShare.None, 16 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough, CreateFileSecurity()))
            {
                if (_writer is null)
                    await JsonSerializer.SerializeAsync(stream, journal, JsonOptions, cancellationToken).ConfigureAwait(false);
                else
                    _writer(stream, journal);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            ValidateFile(temporaryPath);
            var stagedLength = new FileInfo(temporaryPath).Length;
            if (stagedLength <= 0 || stagedLength > MaximumJournalBytes)
                throw new InvalidDataException("The staged original-user journal has an invalid size.");
            ValidateRoot();
            if (File.Exists(_journalPath)) ValidateFile(_journalPath);
            ReplaceFileDurably(temporaryPath, _journalPath);
            temporaryPath = null;
            ValidateFile(_journalPath);
            ValidateRoot();
        }
        finally
        {
            if (temporaryPath is not null && File.Exists(temporaryPath)) File.Delete(temporaryPath);
            _gate.Release();
        }
    }

    /// <summary>Durably records intent and preserves the terminal old journal before machine rollover.</summary>
    public async ValueTask PrepareRolloverAsync(Guid previousRunId, Guid nextRunId, CancellationToken cancellationToken)
    {
        if (previousRunId == Guid.Empty || nextRunId == Guid.Empty || previousRunId == nextRunId)
            throw new ArgumentException("Rollover requires distinct non-empty operation ids.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); RequireLease(); ValidateRoot();
            var record = await ReadRolloverRecordAsync(cancellationToken).ConfigureAwait(false);
            var current = await ReadUnlockedAsync(cancellationToken).ConfigureAwait(false);
            if (record is not null)
            {
                if (record.PreviousRunId != previousRunId || record.NextRunId != nextRunId)
                {
                    if (record.State != UserJournalRolloverState.Committed || current?.RunId != previousRunId ||
                        current.State != UpgradeJournalState.RolledBack || string.IsNullOrWhiteSpace(current.FailureCode) ||
                        !HasNoPendingUserMutations(current))
                        throw new InvalidDataException("A different original-user rollover is already in progress.");
                    record = new UserJournalRolloverRecord(1, previousRunId, nextRunId, UserJournalRolloverState.Prepared);
                    await WriteRolloverRecordAsync(record, cancellationToken).ConfigureAwait(false);
                    _rolloverCheckpoint?.Invoke("prepared");
                }
                else if (record.State == UserJournalRolloverState.Committed)
                    throw new InvalidOperationException("This original-user rollover was already committed.");
            }
            else
            {
                if (current is null || current.RunId != previousRunId || current.State != UpgradeJournalState.RolledBack ||
                    string.IsNullOrWhiteSpace(current.FailureCode) ||
                    !HasNoPendingUserMutations(current))
                    throw new InvalidOperationException("Only a fully rolled-back terminal user operation can be retried.");
                record = new UserJournalRolloverRecord(1, previousRunId, nextRunId, UserJournalRolloverState.Prepared);
                await WriteRolloverRecordAsync(record, cancellationToken).ConfigureAwait(false);
                _rolloverCheckpoint?.Invoke("prepared");
            }

            var archivePath = ArchivePath(previousRunId);
            if (current?.RunId == previousRunId)
                await CreateImmutableArchiveAsync(archivePath, cancellationToken).ConfigureAwait(false);
            else if (current?.RunId != nextRunId)
                throw new InvalidDataException("The current user journal does not match the pending rollover.");
            _rolloverCheckpoint?.Invoke("archived");
        }
        finally { _gate.Release(); }
    }

    /// <summary>Replaces the current document only after the authenticated machine rollover succeeded.</summary>
    public async ValueTask CommitRolloverAsync(Guid previousRunId, Guid nextRunId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); RequireLease(); ValidateRoot();
            var record = await ReadRolloverRecordAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("No prepared user journal rollover exists.");
            if (record.PreviousRunId != previousRunId || record.NextRunId != nextRunId)
                throw new InvalidDataException("The prepared user journal rollover does not match.");
            if (record.State == UserJournalRolloverState.Committed) return;
            var current = await ReadUnlockedAsync(cancellationToken).ConfigureAwait(false);
            if (current?.RunId == previousRunId)
            {
                if (current.State != UpgradeJournalState.RolledBack || string.IsNullOrWhiteSpace(current.FailureCode) ||
                    !HasNoPendingUserMutations(current) || !File.Exists(ArchivePath(previousRunId)))
                    throw new InvalidDataException("The old user journal is no longer safe to roll over.");
                var pristine = new UpgradeJournalDocument(UpgradeJournalDocument.CurrentSchemaVersion, nextRunId, 0,
                    UpgradeJournalState.InProgress, UpgradePhase.Preflight, []);
                await WriteJournalReplacementAsync(pristine, cancellationToken).ConfigureAwait(false);
                _rolloverCheckpoint?.Invoke("next-journal");
            }
            else if (current?.RunId != nextRunId || current.Revision != 0 || current.Events.Count != 0 || current.Recovery is not null)
                throw new InvalidDataException("The next user journal is not pristine.");

            await WriteRolloverRecordAsync(record with { State = UserJournalRolloverState.Committed }, cancellationToken).ConfigureAwait(false);
            _rolloverCheckpoint?.Invoke("committed");
        }
        finally { _gate.Release(); }
    }

    public async ValueTask<UserJournalRolloverRecord?> LoadRolloverAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { ThrowIfDisposed(); RequireLease(); ValidateRoot(); return await ReadRolloverRecordAsync(cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public void Dispose() => Interlocked.Exchange(ref _disposed, 1);

    private async ValueTask<UpgradeJournalDocument?> ReadUnlockedAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_journalPath)) return null;
        ValidateFile(_journalPath);
        var info = new FileInfo(_journalPath);
        if (info.Length <= 0 || info.Length > MaximumJournalBytes)
            throw new InvalidDataException("The original-user journal has an invalid size.");
        await using var stream = new FileStream(_journalPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var bounded = new MemoryStream((int)info.Length);
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (bounded.Length + read > MaximumJournalBytes)
                throw new InvalidDataException("The original-user journal exceeds its size limit while reading.");
            bounded.Write(buffer, 0, read);
        }
        if (bounded.Length != info.Length || bounded.Length > MaximumJournalBytes)
            throw new InvalidDataException("The original-user journal changed or exceeded its size limit while reading.");
        var journal = JsonSerializer.Deserialize<UpgradeJournalDocument>(bounded.GetBuffer().AsSpan(0, (int)bounded.Length), JsonOptions)
            ?? throw new InvalidDataException("The original-user journal is empty.");
        if (journal.Revision < 0 || journal.RunId == Guid.Empty)
            throw new InvalidDataException("The original-user journal document is invalid.");
        if (journal.SchemaVersion != UpgradeJournalDocument.CurrentSchemaVersion)
        {
            var manualRecovery = UpgradeJournalDocument.AsManualRecoveryIfUnsupportedInterruptedSchema(journal);
            if (manualRecovery is not null) return manualRecovery;
            throw new InvalidDataException($"Unsupported original-user journal schema {journal.SchemaVersion}.");
        }
        return journal;
    }

    private string ArchivePath(Guid runId) => Path.Combine(_rootPath, $"journal.archive-{runId:N}.json");

    private async ValueTask<UserJournalRolloverRecord?> ReadRolloverRecordAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_rolloverPath)) return null;
        ValidateFile(_rolloverPath);
        var info = new FileInfo(_rolloverPath);
        if (info.Length is <= 0 or > 4096) throw new InvalidDataException("The user rollover record has an invalid size.");
        await using var stream = new FileStream(_rolloverPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var record = await JsonSerializer.DeserializeAsync<UserJournalRolloverRecord>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The user rollover record is empty.");
        if (record.SchemaVersion != 1 || record.PreviousRunId == Guid.Empty || record.NextRunId == Guid.Empty ||
            record.PreviousRunId == record.NextRunId || !Enum.IsDefined(record.State))
            throw new InvalidDataException("The user rollover record is corrupt.");
        return record;
    }

    private async ValueTask WriteRolloverRecordAsync(UserJournalRolloverRecord record, CancellationToken cancellationToken)
    {
        var temporaryPath = Path.Combine(_rootPath, $".rollover.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = FileSystemAclExtensions.Create(new FileInfo(temporaryPath), FileMode.CreateNew,
                             FileSystemRights.Write, FileShare.None, 4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough, CreateFileSecurity()))
            {
                await JsonSerializer.SerializeAsync(stream, record, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            ValidateFile(temporaryPath);
            ValidateRoot();
            if (File.Exists(_rolloverPath)) ValidateFile(_rolloverPath);
            ReplaceFileDurably(temporaryPath, _rolloverPath);
            ValidateFile(_rolloverPath);
        }
        finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
    }

    private async ValueTask CreateImmutableArchiveAsync(string archivePath, CancellationToken cancellationToken)
    {
        if (File.Exists(archivePath))
        {
            ValidateFile(archivePath);
            var existing = await File.ReadAllBytesAsync(archivePath, cancellationToken).ConfigureAwait(false);
            var current = await File.ReadAllBytesAsync(_journalPath, cancellationToken).ConfigureAwait(false);
            if (!existing.AsSpan().SequenceEqual(current)) throw new InvalidDataException("The prior journal archive conflicts with the current journal.");
            return;
        }
        var temporaryPath = Path.Combine(_rootPath, $".archive.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var source = new FileStream(_journalPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                             16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var target = FileSystemAclExtensions.Create(new FileInfo(temporaryPath), FileMode.CreateNew,
                             FileSystemRights.Write, FileShare.None, 16 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough, CreateFileSecurity()))
            {
                await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
                await target.FlushAsync(cancellationToken).ConfigureAwait(false);
                target.Flush(flushToDisk: true);
            }
            ValidateFile(temporaryPath);
            ValidateRoot();
            if (!MoveFileEx(temporaryPath, archivePath, MoveFileFlags.WriteThrough))
            {
                var error = Marshal.GetLastWin32Error();
                if (File.Exists(archivePath))
                {
                    ValidateFile(archivePath);
                    var archived = await File.ReadAllBytesAsync(archivePath, cancellationToken).ConfigureAwait(false);
                    var current = await File.ReadAllBytesAsync(_journalPath, cancellationToken).ConfigureAwait(false);
                    if (archived.AsSpan().SequenceEqual(current)) return;
                }
                throw new Win32Exception(error, "The original-user journal archive could not be committed atomically.");
            }
            ValidateFile(archivePath);
        }
        finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
    }

    private async ValueTask WriteJournalReplacementAsync(UpgradeJournalDocument journal, CancellationToken cancellationToken)
    {
        var temporaryPath = Path.Combine(_rootPath, $".journal.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = FileSystemAclExtensions.Create(new FileInfo(temporaryPath), FileMode.CreateNew,
                             FileSystemRights.Write, FileShare.None, 16 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough, CreateFileSecurity()))
            {
                await JsonSerializer.SerializeAsync(stream, journal, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            ValidateFile(temporaryPath);
            ValidateRoot();
            ReplaceFileDurably(temporaryPath, _journalPath);
            ValidateFile(_journalPath);
        }
        finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
    }

    private static bool HasNoPendingUserMutations(UpgradeJournalDocument journal)
    {
        foreach (var intent in journal.Events.Where(item => item.Kind == UpgradeJournalEventKind.MutationIntent && item.Mutation is not null))
        {
            var completed = journal.Events.Any(item => item.Sequence > intent.Sequence && item.Mutation == intent.Mutation &&
                item.Kind is UpgradeJournalEventKind.MutationPrepared or UpgradeJournalEventKind.MutationApplied or
                    UpgradeJournalEventKind.MutationCompensated or UpgradeJournalEventKind.MutationFailed);
            if (!completed) return false;
        }
        return true;
    }

    private void EnsureRoot()
    {
        ValidatePathComponents(_rootBoundary);
        if (!Directory.Exists(_rootBoundary))
            throw new DirectoryNotFoundException("The trusted original-user journal boundary is missing.");
        var relative = Path.GetRelativePath(_rootBoundary, _rootPath);
        var current = _rootBoundary;
        foreach (var component in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            ValidatePathComponents(current);
            if (!Directory.Exists(current))
                FileSystemAclExtensions.Create(new DirectoryInfo(current), CreateDirectorySecurity());
            ValidateDirectory(current);
        }
    }

    private void ValidateRoot()
    {
        ValidatePathComponents(_rootBoundary);
        var relative = Path.GetRelativePath(_rootBoundary, _rootPath);
        var current = _rootBoundary;
        foreach (var component in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            ValidatePathComponents(current);
            ValidateDirectory(current);
        }
    }

    private void ValidateDirectory(string path)
    {
        var info = new DirectoryInfo(path);
        if (!info.Exists || (info.Attributes & FileAttributes.Directory) == 0)
            throw new InvalidDataException("The original-user journal root is missing or is not a directory.");
        ValidateSecurity(FileSystemAclExtensions.GetAccessControl(info, AccessControlSections.Owner | AccessControlSections.Access), path, true);
    }

    private void ValidateFile(string path)
    {
        ValidatePathComponents(path);
        var info = new FileInfo(path);
        if (!info.Exists || (info.Attributes & FileAttributes.Directory) != 0)
            throw new FileNotFoundException("An original-user journal file is missing.", path);
        ValidateSecurity(FileSystemAclExtensions.GetAccessControl(info, AccessControlSections.Owner | AccessControlSections.Access), path, false);
    }

    private FileStream OpenLease() => OpenProtectedExclusive(_leasePath);

    private FileStream OpenProtectedExclusive(string path)
    {
        if (File.Exists(path))
        {
            ValidateFile(path);
            return new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.WriteThrough);
        }
        try
        {
            return FileSystemAclExtensions.Create(new FileInfo(path), FileMode.CreateNew,
                FileSystemRights.Read | FileSystemRights.Write, FileShare.None, 1, FileOptions.WriteThrough,
                CreateFileSecurity());
        }
        catch (IOException) when (File.Exists(path))
        {
            ValidateFile(path);
            return new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.WriteThrough);
        }
    }

    private void ValidateSecurity(FileSystemSecurity security, string path, bool directory)
    {
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || owner.Value != _userSid.Value)
            throw new UnauthorizedAccessException($"Journal path '{path}' is not owned by the original user.");
        if (!security.AreAccessRulesProtected)
            throw new UnauthorizedAccessException($"Journal path '{path}' inherits its DACL.");
        var expected = new HashSet<string>([ _userSid.Value, SystemSid.Value, AdministratorsSid.Value ], StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            var sid = rule.IdentityReference.Value;
            if (rule.IsInherited || rule.AccessControlType != AccessControlType.Allow || !expected.Contains(sid) ||
                (rule.FileSystemRights & FileSystemRights.FullControl) != FileSystemRights.FullControl ||
                (directory && (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0))
                throw new UnauthorizedAccessException($"Journal path '{path}' has a non-canonical ACL.");
            seen.Add(sid);
        }
        if (!seen.SetEquals(expected)) throw new UnauthorizedAccessException($"Journal path '{path}' lacks a required ACL principal.");
    }

    private FileSecurity CreateFileSecurity()
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(_userSid);
        foreach (var sid in new[] { _userSid, SystemSid, AdministratorsSid })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        return security;
    }

    private DirectorySecurity CreateDirectorySecurity()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(_userSid);
        foreach (var sid in new[] { _userSid, SystemSid, AdministratorsSid })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    private static void ValidatePathComponents(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? throw new InvalidDataException("Journal path has no filesystem root.");
        var current = root;
        foreach (var component in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            if (!File.Exists(current) && !Directory.Exists(current)) continue;
            var info = File.Exists(current) ? (FileSystemInfo)new FileInfo(current) : new DirectoryInfo(current);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.LinkTarget is not null)
                throw new InvalidDataException("Original-user journal paths cannot contain reparse points.");
        }
    }

    private static string GetDefaultRoot()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local)) throw new InvalidOperationException("LocalAppData is unavailable for the current user.");
        return Path.Combine(local, "Structura", "Connector", "UpgradeJournal");
    }

    private static bool IsUnderLocalAppData(string path)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local)) return false;
        return IsStrictlyUnder(Path.GetFullPath(local), path);
    }

    private static bool IsStrictlyUnder(string parent, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(parent), Path.GetFullPath(path));
        return relative != "." && relative != ".." &&
               !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
               !Path.IsPathRooted(relative);
    }

    private void RequireLease()
    {
        if (Volatile.Read(ref _leaseHeld) == 0) throw new InvalidOperationException("Load and save require this store's active lease.");
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(WindowsUserUpgradeJournalStore));
    }

    private async ValueTask ReleaseLeaseAsync(FileStream stream)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { stream.Dispose(); Volatile.Write(ref _leaseHeld, 0); }
        finally { _gate.Release(); }
    }

    private static void ReplaceFileDurably(string source, string destination)
    {
        if (!MoveFileEx(source, destination, MoveFileFlags.ReplaceExisting | MoveFileFlags.WriteThrough))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The original-user journal could not be committed atomically.");
    }

    [Flags] private enum MoveFileFlags : uint { ReplaceExisting = 1, WriteThrough = 8 }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string existingFileName, string newFileName, MoveFileFlags flags);

    private sealed class UserJournalLease(WindowsUserUpgradeJournalStore store, FileStream stream) : IUpgradeJournalLease
    {
        private int _released;
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) await store.ReleaseLeaseAsync(stream).ConfigureAwait(false);
        }
    }
}

public enum UserJournalRolloverState { Prepared, Committed }

public sealed record UserJournalRolloverRecord(
    int SchemaVersion, Guid PreviousRunId, Guid NextRunId, UserJournalRolloverState State);
