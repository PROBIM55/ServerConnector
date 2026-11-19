namespace Connector.Updater;

/// <summary>Timing rules of the service (§3.2 of the update design and the K9 task), in one place.</summary>
internal static class UpdateSchedule
{
    public static readonly TimeSpan FirstCheckMinimum = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan FirstCheckSpread = TimeSpan.FromMinutes(8);
    public static readonly TimeSpan RegularInterval = TimeSpan.FromHours(4);
    public static readonly TimeSpan RegularJitter = TimeSpan.FromMinutes(20);
    /// <summary>A failed check is retried sooner, then the regular cadence takes over.</summary>
    public static readonly TimeSpan[] CheckRetryDelays = [TimeSpan.FromMinutes(10), TimeSpan.FromHours(1)];
    /// <summary>«Проверь сейчас» is honoured at most once a minute.</summary>
    public static readonly TimeSpan CheckRequestMinimumSpacing = TimeSpan.FromMinutes(1);
    /// <summary>A check request is considered only if its timestamp is this recent (and not from the future).</summary>
    public static readonly TimeSpan CheckRequestMaximumAge = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan ClockSkewAllowance = TimeSpan.FromMinutes(2);
    /// <summary>«Занят» can postpone an installation by at most this long after the update became ready.</summary>
    public static readonly TimeSpan MaximumBusyDeferral = TimeSpan.FromHours(8);
    public static readonly TimeSpan ApplicationCloseGrace = TimeSpan.FromSeconds(60);
    /// <summary>A live installer is awaited in slices (the status heartbeat keeps running meanwhile).</summary>
    public static readonly TimeSpan InstallerWaitSlice = TimeSpan.FromMinutes(10);
    /// <summary>Past this the attempt counts as failed once; a still running installer is awaited, never doubled.</summary>
    public static readonly TimeSpan InstallerAbsoluteLimit = TimeSpan.FromHours(3);
    /// <summary>The same (version, sha256) failing verification this often stops downloading until the description changes.</summary>
    public const int MaximumHashMismatches = 3;
    /// <summary>Pauses after the 1st, 2nd and 3rd failed installation of a version; the next failure marks it problematic.</summary>
    public static readonly TimeSpan[] InstallRetryDelays = [TimeSpan.FromMinutes(10), TimeSpan.FromHours(1), TimeSpan.FromHours(6)];
    public static readonly TimeSpan IdlePoll = TimeSpan.FromSeconds(15);
    /// <summary>A step failing this long while an installation is recorded withdraws «installing».</summary>
    public static readonly TimeSpan EngineStallLimit = TimeSpan.FromMinutes(10);
    /// <summary>The heartbeat confirms the engine only if it completed a step this recently.</summary>
    public static readonly TimeSpan EngineSilenceLimit = TimeSpan.FromMinutes(3);
    /// <summary>Random addition to the catalog's Retry-After when it is serving its download limit.</summary>
    public static readonly TimeSpan BusyJitterMaximum = TimeSpan.FromSeconds(60);
    /// <summary>Growing pause after a broken download (5xx, connection lost, wrong bytes); the last value repeats.</summary>
    public static readonly TimeSpan[] DownloadRetryDelays = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromHours(1)];

    public static TimeSpan BusyJitter(double random) => TimeSpan.FromTicks((long)(BusyJitterMaximum.Ticks * Clamp01(random)));

    public static TimeSpan DownloadRetryDelay(int consecutiveFailures) =>
        DownloadRetryDelays[Math.Clamp(consecutiveFailures, 1, DownloadRetryDelays.Length) - 1];

    public static TimeSpan FirstCheckDelay(double random) =>
        FirstCheckMinimum + TimeSpan.FromTicks((long)(FirstCheckSpread.Ticks * Clamp01(random)));

    public static TimeSpan RegularDelay(double random) =>
        RegularInterval + TimeSpan.FromTicks((long)(RegularJitter.Ticks * (Clamp01(random) * 2 - 1)));

    public static TimeSpan CheckRetryDelay(int consecutiveFailures, double random) =>
        consecutiveFailures >= 1 && consecutiveFailures <= CheckRetryDelays.Length
            ? CheckRetryDelays[consecutiveFailures - 1]
            : RegularDelay(random);

    private static double Clamp01(double value) => double.IsNaN(value) ? 0 : Math.Clamp(value, 0, 0.999999);
}
