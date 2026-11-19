using System.Text;
using Connector.Updates;

namespace Connector.Updater;

/// <summary>Atomic replacement of a file the service alone writes (both directories are closed to users).</summary>
internal static class AtomicFile
{
    public static void Write(string path, byte[] bytes)
    {
        var directory = Path.GetDirectoryName(path)!;
        var temporary = Path.Combine(directory, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { }
        }
    }

    public static byte[]? ReadBounded(string path, int maximumBytes)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > maximumBytes) return null;
            var buffer = new byte[stream.Length];
            stream.ReadExactly(buffer);
            return buffer;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
}

/// <summary>
/// The service state file. It is believed only if <see cref="IFileTrust"/> accepts the open file (a file anybody
/// but SYSTEM/Administrators could have written is deleted, never read); even then it only says what to resume —
/// installing still requires a description signed and fetched in the current run.
/// </summary>
internal sealed class FileStateStore(string path, IUpdaterLog log, IFileTrust trust) : IStateStore
{
    private const int MaximumBytes = 256 * 1024;

    public UpdaterState Load()
    {
        byte[]? bytes = null;
        var untrusted = false;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!trust.IsTrusted(stream)) untrusted = true;
            else if (stream.Length <= MaximumBytes)
            {
                bytes = new byte[stream.Length];
                stream.ReadExactly(bytes);
            }
        }
        catch (FileNotFoundException) { return new UpdaterState(); }
        catch (DirectoryNotFoundException) { return new UpdaterState(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { untrusted = true; }
        if (untrusted)
        {
            log.Warning("state-reset: service state file is not trustworthy; deleted.");
            try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return new UpdaterState();
        }
        var state = bytes is null ? null : UpdaterStateCodec.TryDeserialize(bytes);
        if (state is null) log.Warning("Service state unreadable; starting clean.");
        return state ?? new UpdaterState();
    }

    public void Save(UpdaterState state) => AtomicFile.Write(path, UpdaterStateCodec.Serialize(state));
}

/// <summary>
/// Writes <c>update-status.json</c> when its content changed and, as a heartbeat, at least every
/// <see cref="ActiveHeartbeat"/> while downloading or installing and every <see cref="IdleHeartbeat"/> otherwise —
/// also while the engine is blocked waiting for an installer (<see cref="Beat"/> runs on its own timer).
/// The application believes «installing» only while this heartbeat is fresh.
/// </summary>
internal sealed class FileStatusSink : IStatusSink, IDisposable
{
    public static readonly TimeSpan ActiveHeartbeat = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan IdleHeartbeat = TimeSpan.FromMinutes(15);
    private readonly object _gate = new();
    private readonly Action<string, byte[]> _write;
    private readonly string _path;
    private readonly Func<DateTimeOffset> _now;
    private readonly Timer? _timer;
    private byte[]? _lastComparable;
    private UpdateStatus? _last;
    private DateTimeOffset _engineTouchedAtUtc = DateTimeOffset.MinValue;
    private Func<bool>? _installerAlive;

    public FileStatusSink(string path, bool heartbeat = true) : this(path, () => DateTimeOffset.UtcNow, AtomicFile.Write, heartbeat) { }

    internal FileStatusSink(string path, Func<DateTimeOffset> now, Action<string, byte[]> write, bool heartbeat)
    {
        _path = path;
        _now = now;
        _write = write;
        if (heartbeat) _timer = new Timer(_ => SafeBeat(), null, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20));
    }

    public void Publish(UpdateStatus status)
    {
        lock (_gate)
        {
            var comparable = UpdateStatusCodec.Serialize(status with { WrittenAtUtc = DateTimeOffset.UnixEpoch });
            if (_lastComparable is not null && _last is not null && comparable.AsSpan().SequenceEqual(_lastComparable) &&
                status.WrittenAtUtc - _last.WrittenAtUtc < Interval(status.Phase))
                return;
            _write(_path, UpdateStatusCodec.Serialize(status));
            _lastComparable = comparable;
            _last = status;
        }
    }

    public void Touch()
    {
        lock (_gate) _engineTouchedAtUtc = _now();
    }

    public void SetActiveInstaller(Func<bool>? isAlive)
    {
        lock (_gate)
        {
            _installerAlive = isAlive;
            if (isAlive is not null) _engineTouchedAtUtc = _now();
        }
    }

    /// <summary>Rewrites the last status with a fresh time when its heartbeat interval passed.</summary>
    internal void Beat()
    {
        lock (_gate)
        {
            if (_last is null) return;
            var now = _now();
            if (now - _last.WrittenAtUtc < Interval(_last.Phase)) return;
            // The heartbeat vouches for progress, not for a living process: «installing» only while the installer this
            // service awaits is alive (the engine is blocked in that wait); otherwise only after a recent engine step.
            if (_last.Phase == UpdatePhase.Installing)
            {
                bool alive;
                try { alive = _installerAlive?.Invoke() == true; }
                catch (ObjectDisposedException) { alive = false; }
                if (!alive || now - _engineTouchedAtUtc > UpdateSchedule.InstallerWaitSlice + UpdateSchedule.EngineSilenceLimit) return;
            }
            else if (now - _engineTouchedAtUtc > UpdateSchedule.EngineSilenceLimit) return;
            var fresh = _last with { WrittenAtUtc = now };
            _write(_path, UpdateStatusCodec.Serialize(fresh));
            _last = fresh;
        }
    }

    private void SafeBeat()
    {
        try { Beat(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    internal static TimeSpan Interval(UpdatePhase phase) =>
        phase is UpdatePhase.Installing or UpdatePhase.Downloading ? ActiveHeartbeat : IdleHeartbeat;

    public void Dispose() => _timer?.Dispose();
}

/// <summary>
/// The service journal: one line per event, versions and codes only (no paths from users, no server text,
/// no secrets — the service has none). Rotated at 1 MiB, three generations.
/// </summary>
internal sealed class FileUpdaterLog : IUpdaterLog
{
    private const long MaximumBytes = 1024 * 1024;
    private const int Generations = 3;
    private readonly object _gate = new();
    private readonly string _path;
    private readonly bool _echo;

    public FileUpdaterLog(string directory, bool echoToConsole)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "updater.log");
        _echo = echoToConsole;
    }

    public void Info(string message) => Write("INFO", message);
    public void Warning(string message) => Write("WARN", message);

    private void Write(string level, string message)
    {
        var line = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'") + " " + level + " " + Sanitize(message) + Environment.NewLine;
        lock (_gate)
        {
            try
            {
                Rotate();
                File.AppendAllText(_path, line, Encoding.UTF8);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            if (_echo) Console.Write(line);
        }
    }

    private void Rotate()
    {
        var info = new FileInfo(_path);
        if (!info.Exists || info.Length < MaximumBytes) return;
        for (var generation = Generations - 1; generation >= 1; generation--)
        {
            var older = _path + "." + generation;
            var newer = generation == 1 ? _path : _path + "." + (generation - 1);
            if (File.Exists(newer)) File.Move(newer, older, overwrite: true);
        }
    }

    private static string Sanitize(string message)
    {
        var builder = new StringBuilder(Math.Min(message.Length, 500));
        foreach (var character in message.Take(500)) builder.Append(char.IsControl(character) ? ' ' : character);
        return builder.ToString();
    }
}
