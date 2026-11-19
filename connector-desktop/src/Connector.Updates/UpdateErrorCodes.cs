namespace Connector.Updates;

/// <summary>
/// Fixed, closed set of failure codes. Only these strings ever reach the status file and the journal,
/// so neither carries server text, paths or exception messages.
/// </summary>
public static class UpdateErrorCodes
{
    public const string Network = "network";
    public const string HttpStatus = "http-status";
    public const string RedirectRefused = "redirect-refused";
    public const string DescriptionTooLarge = "description-too-large";
    public const string SignatureInvalid = "signature-invalid";
    public const string DescriptionInvalid = "description-invalid";
    public const string ChannelMismatch = "channel-mismatch";
    public const string ChannelInvalid = "channel-invalid";
    public const string BelowMinimumVersion = "below-minimum-version";
    public const string DownloadSizeMismatch = "download-size-mismatch";
    public const string DownloadHashMismatch = "download-hash-mismatch";
    public const string DownloadInvalidRange = "download-invalid-range";
    /// <summary>The catalog serves its download limit; retried after Retry-After, not an installation failure.</summary>
    public const string DownloadBusy = "download-busy";
    public const string Storage = "storage";
    public const string DiskSpace = "disk-space";
    /// <summary>The service's folders are not trustworthy; it does nothing until they are.</summary>
    public const string StorageUntrusted = "storage-untrusted";
    /// <summary>The engine keeps failing while an installation is recorded; «installing» is withdrawn.</summary>
    public const string EngineStalled = "engine-stalled";
    public const string InstalledVersionUnknown = "installed-version-unknown";
    public const string InstallerStartFailed = "installer-start-failed";
    public const string InstallerFailed = "installer-failed";
    public const string InstallerTimeout = "installer-timeout";
    public const string InstalledVersionMismatch = "installed-version-mismatch";
    public const string VersionMarkedProblem = "version-problem";
    public const string Internal = "internal";

    private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        Network, HttpStatus, RedirectRefused, DescriptionTooLarge, SignatureInvalid, DescriptionInvalid,
        ChannelMismatch, ChannelInvalid, BelowMinimumVersion, DownloadSizeMismatch, DownloadHashMismatch,
        DownloadInvalidRange, DownloadBusy, Storage, DiskSpace, StorageUntrusted, EngineStalled, InstalledVersionUnknown, InstallerStartFailed, InstallerFailed,
        InstallerTimeout, InstalledVersionMismatch, VersionMarkedProblem, Internal
    };

    public static bool IsKnown(string? code) => code is not null && Known.Contains(code);
}

/// <summary>A refused update step; <see cref="Code"/> is always one of <see cref="UpdateErrorCodes"/>.</summary>
public sealed class UpdateRejectedException : Exception
{
    public UpdateRejectedException(string code) : base(code)
    {
        Code = UpdateErrorCodes.IsKnown(code) ? code : UpdateErrorCodes.Internal;
    }

    public UpdateRejectedException(string code, Exception inner) : base(code, inner)
    {
        Code = UpdateErrorCodes.IsKnown(code) ? code : UpdateErrorCodes.Internal;
    }

    public string Code { get; }
}
