using System.Text.Json;
using System.Text.Json.Serialization;

namespace Platform.Connector.Core;

public sealed class FileRequestIdempotencyStore : IRequestIdempotencyStore
{
    private const int CurrentSchemaVersion = 2;
    private const int DefaultMaxPublishedEntries = 2048;
    private const long CompactAboveBytes = 16L * 1024L * 1024L;

    private readonly string _statePath;
    private readonly int _maxPublishedEntries;
    private readonly object _gate = new();
    private readonly Dictionary<string, StoredTerminalStatus> _items;
    private readonly Dictionary<string, StoredLocalJob> _localJobs;
    private long _nextLocalSequence;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public FileRequestIdempotencyStore(string statePath, int maxPublishedEntries = DefaultMaxPublishedEntries)
    {
        _statePath = statePath;
        _maxPublishedEntries = Math.Max(1, maxPublishedEntries);
        var loaded = Load();
        _items = new Dictionary<string, StoredTerminalStatus>(loaded.Terminals, StringComparer.OrdinalIgnoreCase);
        _localJobs = new Dictionary<string, StoredLocalJob>(loaded.LocalJobs, StringComparer.OrdinalIgnoreCase);
        _nextLocalSequence = Math.Max(
            loaded.NextLocalSequence,
            _localJobs.Count == 0 ? 0 : _localJobs.Values.Max(item => item.Sequence));

        var changed = CompactPublishedResultsUnsafe() | PrunePublishedUnsafe();
        if (changed || (File.Exists(_statePath) && new FileInfo(_statePath).Length > CompactAboveBytes))
        {
            SaveUnsafe();
        }
    }

    public Task<StoredTerminalStatus?> TryGetAsync(string requestId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _items.TryGetValue(requestId, out var item);
            return Task.FromResult(item);
        }
    }

    public Task<IReadOnlyList<StoredTerminalStatus>> GetPendingAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            IReadOnlyList<StoredTerminalStatus> pending = _items.Values
                .Where(item => !item.Published)
                .OrderBy(item => item.SavedAtUtc)
                .ToArray();
            return Task.FromResult(pending);
        }
    }

    public Task<IReadOnlyList<StoredTerminalStatus>> GetTerminalHistoryAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            IReadOnlyList<StoredTerminalStatus> history = _items.Values
                .OrderBy(item => item.SavedAtUtc)
                .ToArray();
            return Task.FromResult(history);
        }
    }

    public Task<StoredLocalStateSnapshot> GetLocalStateAsync(
        string deviceId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            IReadOnlyList<StoredLocalJob> accepted = _localJobs.Values
                .Where(item => string.Equals(item.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(item => item.Sequence)
                .ToArray();
            IReadOnlyList<StoredTerminalStatus> terminals = _items.Values
                .Where(item => item.Envelope.Scope?.ScopeKind == ConnectorScopeKind.DeviceLocal
                               && string.Equals(item.Envelope.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(item => item.SavedAtUtc)
                .ToArray();
            return Task.FromResult(new StoredLocalStateSnapshot(accepted, terminals));
        }
    }

    public Task<IReadOnlyList<StoredLocalJob>> GetLocalJobsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            IReadOnlyList<StoredLocalJob> jobs = _localJobs.Values
                .OrderBy(item => item.Sequence)
                .ToArray();
            return Task.FromResult(jobs);
        }
    }

    public Task SaveLocalQueuedAsync(
        string deviceId,
        ConnectorJobEnvelope job,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedDeviceId = string.IsNullOrWhiteSpace(deviceId)
            ? throw new ArgumentException("Local device identity is required.", nameof(deviceId))
            : deviceId.Trim();
        ValidateLocalJobEnvelope(job);

        lock (_gate)
        {
            if (_items.ContainsKey(job.RequestId) || _localJobs.ContainsKey(job.RequestId))
            {
                throw new InvalidOperationException($"Request '{job.RequestId}' already has durable state.");
            }

            var now = DateTime.UtcNow;
            var sequence = checked(++_nextLocalSequence);
            _localJobs.Add(
                job.RequestId,
                new StoredLocalJob(
                    normalizedDeviceId,
                    job,
                    StoredLocalJobState.Queued,
                    sequence,
                    now,
                    now));
            SaveUnsafe();
        }

        return Task.CompletedTask;
    }

    public Task MarkLocalRunningAsync(string requestId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_localJobs.TryGetValue(requestId, out var existing))
            {
                throw new InvalidOperationException($"Durable local request '{requestId}' was not found.");
            }
            if (existing.State == StoredLocalJobState.Running)
            {
                return Task.CompletedTask;
            }
            if (existing.State != StoredLocalJobState.Queued)
            {
                throw new InvalidDataException($"Durable local request '{requestId}' has invalid state '{existing.State}'.");
            }

            _localJobs[requestId] = existing with
            {
                State = StoredLocalJobState.Running,
                UpdatedAtUtc = DateTime.UtcNow
            };
            SaveUnsafe();
        }

        return Task.CompletedTask;
    }

    public Task SaveTerminalAsync(
        ConnectorJobStatusEnvelope envelope,
        bool published,
        CancellationToken cancellationToken)
        => SaveTerminalAsync(envelope, published, cancellationToken, remoteAuthority: null, sourceJob: null);

    public Task SaveTerminalAsync(
        ConnectorJobStatusEnvelope envelope,
        bool published,
        CancellationToken cancellationToken,
        string? remoteAuthority)
        => SaveTerminalAsync(envelope, published, cancellationToken, remoteAuthority, sourceJob: null);

    public Task SaveTerminalAsync(
        ConnectorJobStatusEnvelope envelope,
        bool published,
        CancellationToken cancellationToken,
        string? remoteAuthority,
        ConnectorJobEnvelope? sourceJob)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateTerminal(envelope, sourceJob);
        lock (_gate)
        {
            _items[envelope.RequestId] = new StoredTerminalStatus(
                envelope,
                published,
                DateTime.UtcNow,
                remoteAuthority,
                sourceJob);
            _localJobs.Remove(envelope.RequestId);
            CompactPublishedResultsUnsafe();
            PrunePublishedUnsafe();
            SaveUnsafe();
        }

        return Task.CompletedTask;
    }

    public Task MarkPublishedAsync(string requestId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_items.TryGetValue(requestId, out var existing))
            {
                var retainLocalResult = existing.Envelope.Scope?.ScopeKind == ConnectorScopeKind.DeviceLocal;
                _items[requestId] = existing with
                {
                    Envelope = retainLocalResult
                        ? existing.Envelope
                        : existing.Envelope with { Result = null },
                    Published = true,
                    SavedAtUtc = DateTime.UtcNow,
                };
                PrunePublishedUnsafe();
                SaveUnsafe();
            }
        }

        return Task.CompletedTask;
    }

    private LoadedState Load()
    {
        if (!File.Exists(_statePath))
        {
            return LoadedState.Empty;
        }

        var text = File.ReadAllText(_statePath);
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidDataException($"Connector durable state '{_statePath}' is empty.");
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("Connector durable state root must be a JSON object.");
            }

            LoadedState loaded;
            if (document.RootElement.TryGetProperty("schemaVersion", out var schemaVersion)
                && schemaVersion.ValueKind == JsonValueKind.Number)
            {
                var state = document.RootElement.Deserialize<FileState>(JsonOptions)
                    ?? throw new InvalidDataException("Connector durable state is missing its body.");
                if (state.SchemaVersion != CurrentSchemaVersion)
                {
                    throw new InvalidDataException($"Unsupported connector durable state schema '{state.SchemaVersion}'.");
                }
                if (state.Terminals is null || state.LocalJobs is null || state.NextLocalSequence < 0)
                {
                    throw new InvalidDataException("Connector durable state is incomplete.");
                }
                loaded = new LoadedState(state.Terminals, state.LocalJobs, state.NextLocalSequence);
            }
            else
            {
                var legacy = document.RootElement.Deserialize<Dictionary<string, StoredTerminalStatus>>(JsonOptions)
                    ?? throw new InvalidDataException("Legacy connector durable state is invalid.");
                loaded = new LoadedState(legacy, new Dictionary<string, StoredLocalJob>(), 0);
            }

            ValidateLoadedState(loaded);
            return loaded;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new InvalidDataException(
                $"Connector durable state '{_statePath}' is corrupt and will not be executed.",
                ex);
        }
    }

    private void SaveUnsafe()
    {
        var directory = Path.GetDirectoryName(_statePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var state = new FileState(CurrentSchemaVersion, _items, _localJobs, _nextLocalSequence);
        var tempPath = _statePath + ".tmp";
        using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, state, JsonOptions);
            stream.Flush(flushToDisk: true);
        }
        File.Move(tempPath, _statePath, overwrite: true);
    }

    private bool CompactPublishedResultsUnsafe()
    {
        var changed = false;
        foreach (var (requestId, existing) in _items.ToArray())
        {
            if (!existing.Published
                || existing.Envelope.Result is null
                || existing.Envelope.Scope?.ScopeKind == ConnectorScopeKind.DeviceLocal)
            {
                continue;
            }
            _items[requestId] = existing with { Envelope = existing.Envelope with { Result = null } };
            changed = true;
        }
        return changed;
    }

    private bool PrunePublishedUnsafe()
    {
        var obsolete = _items
            .Where(item => item.Value.Published
                           && item.Value.Envelope.Scope?.ScopeKind != ConnectorScopeKind.DeviceLocal)
            .OrderByDescending(item => item.Value.SavedAtUtc)
            .Skip(_maxPublishedEntries)
            .Select(item => item.Key)
            .ToArray();

        foreach (var requestId in obsolete)
        {
            _items.Remove(requestId);
        }
        return obsolete.Length > 0;
    }

    private static void ValidateLoadedState(LoadedState state)
    {
        foreach (var (requestId, terminal) in state.Terminals)
        {
            if (terminal is null
                || terminal.Envelope is null
                || !string.Equals(requestId, terminal.Envelope.RequestId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Terminal record '{requestId}' has inconsistent identity.");
            }
            ValidateTerminal(terminal.Envelope, terminal.SourceJob);
        }

        foreach (var (requestId, localJob) in state.LocalJobs)
        {
            if (localJob is null
                || localJob.Job is null
                || !string.Equals(requestId, localJob.Job.RequestId, StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(localJob.DeviceId)
                || localJob.Sequence <= 0
                || !Enum.IsDefined(localJob.State))
            {
                throw new InvalidDataException($"Local queue record '{requestId}' is invalid.");
            }
            ValidateLocalJobEnvelope(localJob.Job);
        }

        if (state.Terminals.Keys.Intersect(state.LocalJobs.Keys, StringComparer.OrdinalIgnoreCase).Any())
        {
            throw new InvalidDataException("A request cannot be both terminal and queued.");
        }
        if (state.LocalJobs.Count > 0 && state.NextLocalSequence < state.LocalJobs.Values.Max(item => item.Sequence))
        {
            throw new InvalidDataException("Local queue sequence is inconsistent.");
        }
    }

    private static void ValidateTerminal(
        ConnectorJobStatusEnvelope envelope,
        ConnectorJobEnvelope? sourceJob)
    {
        if (envelope.SchemaVersion is not (1 or 2)
            || string.IsNullOrWhiteSpace(envelope.RequestId)
            || !Enum.IsDefined(envelope.Provider)
            || !Enum.IsDefined(envelope.Status)
            || envelope.Status is JobStatus.Queued or JobStatus.Picked or JobStatus.Running)
        {
            throw new InvalidDataException("Only a terminal job status can be stored as a terminal result.");
        }
        if (sourceJob is not null)
        {
            ValidateLocalJobEnvelope(sourceJob);
            if (!string.Equals(sourceJob.RequestId, envelope.RequestId, StringComparison.OrdinalIgnoreCase)
                || sourceJob.Scope != envelope.Scope)
            {
                throw new InvalidDataException("Stored local source job does not match its terminal result.");
            }
        }
    }

    private static void ValidateLocalJobEnvelope(ConnectorJobEnvelope job)
    {
        if (job.SchemaVersion is not (1 or 2)
            || string.IsNullOrWhiteSpace(job.RequestId)
            || string.IsNullOrWhiteSpace(job.ModuleId)
            || !Enum.IsDefined(job.Provider)
            || !Enum.IsDefined(job.Operation)
            || job.Scope is null
            || job.Scope.ScopeKind != ConnectorScopeKind.DeviceLocal)
        {
            throw new InvalidDataException("Durable local jobs require a request id and DeviceLocal scope.");
        }
    }

    private sealed record FileState(
        int SchemaVersion,
        Dictionary<string, StoredTerminalStatus> Terminals,
        Dictionary<string, StoredLocalJob> LocalJobs,
        long NextLocalSequence);

    private sealed record LoadedState(
        IReadOnlyDictionary<string, StoredTerminalStatus> Terminals,
        IReadOnlyDictionary<string, StoredLocalJob> LocalJobs,
        long NextLocalSequence)
    {
        public static LoadedState Empty { get; } = new(
            new Dictionary<string, StoredTerminalStatus>(),
            new Dictionary<string, StoredLocalJob>(),
            0);
    }
}
