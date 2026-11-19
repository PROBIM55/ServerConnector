using System.IO;
using System.Security.Principal;
using Connector.Updates;

namespace Connector.Desktop.Services;

/// <summary>
/// The application's whole part in updating: read the status of the machine service, and write the two
/// flag files the service honours — «занят до» while a job runs and «проверь сейчас» for the button.
/// The application never downloads, verifies or starts an installer.
/// </summary>
public interface IDesktopUpdateClient
{
    UpdateStatus? ReadStatus();
    /// <summary>True running, false installed but stopped or not installed, null when it cannot be read.</summary>
    bool? IsServiceRunning();
    void RequestCheck(DateTimeOffset now);
    /// <summary>Null clears the flag; the service honours it for at most 8 hours after the update is ready.</summary>
    void SetBusy(DateTimeOffset? until);
    /// <summary>The completed update to announce once after the restart (null when already announced or none).</summary>
    CompletedUpdate? TakeCompletedUpdateAnnouncement(UpdateStatus? status, string? runningVersion);
}

public sealed class DesktopUpdateClient : IDesktopUpdateClient
{
    private readonly UpdateLocations _locations;
    private readonly string _announcedMarker;
    private readonly string? _userSid;
    private DateTimeOffset? _busyWritten;

    public DesktopUpdateClient() : this(UpdateLocations.Machine(), Path.Combine(UnifiedUserStatePaths.Root, "update-announced.txt"),
        WindowsIdentity.GetCurrent().User?.Value)
    {
    }

    internal DesktopUpdateClient(UpdateLocations locations, string announcedMarker, string? userSid)
    {
        _locations = locations;
        _announcedMarker = announcedMarker;
        _userSid = UpdateSignalFiles.IsSid(userSid) ? userSid : null;
    }

    public UpdateStatus? ReadStatus()
    {
        try
        {
            // Share everything so the service can replace the file while it is being read.
            using var stream = new FileStream(_locations.StatusFile, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > UpdateStatus.MaximumBytes) return null;
            var buffer = new byte[stream.Length];
            stream.ReadExactly(buffer);
            return UpdateStatusCodec.TryDeserialize(buffer);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public bool? IsServiceRunning()
    {
        try
        {
            using var service = new System.ServiceProcess.ServiceController(UpdateLocations.ServiceName);
            return service.Status is System.ServiceProcess.ServiceControllerStatus.Running or
                System.ServiceProcess.ServiceControllerStatus.StartPending;
        }
        catch (InvalidOperationException ex) when (ex.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 1060 })
        {
            return false; // ERROR_SERVICE_DOES_NOT_EXIST
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
    }

    public void RequestCheck(DateTimeOffset now)
    {
        if (_userSid is null) throw new InvalidOperationException("Не удалось определить пользователя Windows.");
        Write(UpdateSignalFiles.CheckFileName(_userSid), now);
    }

    public void SetBusy(DateTimeOffset? until)
    {
        if (_userSid is null || until == _busyWritten) return;
        var path = Path.Combine(_locations.Signals, UpdateSignalFiles.BusyFileName(_userSid));
        try
        {
            if (until is { } value) Write(UpdateSignalFiles.BusyFileName(_userSid), value);
            else if (File.Exists(path)) File.Delete(path);
            _busyWritten = until;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public CompletedUpdate? TakeCompletedUpdateAnnouncement(UpdateStatus? status, string? runningVersion)
    {
        var last = status?.LastUpdate;
        if (last is null || !string.Equals(last.Version, runningVersion, StringComparison.Ordinal)) return null;
        try
        {
            if (File.Exists(_announcedMarker) && File.ReadAllText(_announcedMarker).Trim() == last.Version) return null;
            Directory.CreateDirectory(Path.GetDirectoryName(_announcedMarker)!);
            File.WriteAllText(_announcedMarker, last.Version);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        return last;
    }

    private void Write(string fileName, DateTimeOffset value)
    {
        // The folder is created by the installer; files there are the user's own (CREATOR OWNER).
        File.WriteAllBytes(Path.Combine(_locations.Signals, fileName), UpdateSignalFiles.Encode(value));
    }
}

/// <summary>For hosts without a machine installation (UI fixtures): no status, flags are ignored.</summary>
public sealed class NoDesktopUpdateClient : IDesktopUpdateClient
{
    public UpdateStatus? ReadStatus() => null;
    public bool? IsServiceRunning() => null;
    public void RequestCheck(DateTimeOffset now) => throw new InvalidOperationException("Служба обновления недоступна.");
    public void SetBusy(DateTimeOffset? until) { }
    public CompletedUpdate? TakeCompletedUpdateAnnouncement(UpdateStatus? status, string? runningVersion) => null;
}
