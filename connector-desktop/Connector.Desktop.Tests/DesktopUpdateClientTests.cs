using Connector.Desktop.Services;
using Connector.Updates;
using Xunit;

namespace Connector.Desktop.Tests;

public sealed class DesktopUpdateClientTests : IDisposable
{
    private const string Sid = "S-1-5-21-1000-2000-3000-1001";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "k9-desktop-" + Guid.NewGuid().ToString("N"));
    private readonly UpdateLocations _locations;
    private readonly DesktopUpdateClient _client;

    public DesktopUpdateClientTests()
    {
        _locations = new UpdateLocations(_root);
        Directory.CreateDirectory(_locations.UpdatesRoot);
        Directory.CreateDirectory(_locations.Signals);
        _client = new DesktopUpdateClient(_locations, Path.Combine(_root, "user", "update-announced.txt"), Sid);
    }

    [Fact]
    public void Flags_contain_only_a_timestamp_under_the_user_sid()
    {
        var now = new DateTimeOffset(2026, 11, 25, 9, 0, 0, TimeSpan.Zero);
        _client.RequestCheck(now);
        Assert.Equal("2026-11-25T09:00:00Z", File.ReadAllText(Path.Combine(_locations.Signals, "check-" + Sid + ".flag")));
        _client.SetBusy(now.AddMinutes(15));
        var busy = Path.Combine(_locations.Signals, "busy-" + Sid + ".flag");
        Assert.Equal(now.AddMinutes(15), UpdateSignalFiles.TryDecode(File.ReadAllBytes(busy)));
        _client.SetBusy(null);
        Assert.False(File.Exists(busy));
    }

    [Fact]
    public void Status_is_read_tolerantly_and_garbage_means_no_status()
    {
        Assert.Null(_client.ReadStatus());
        File.WriteAllText(_locations.StatusFile, "garbage");
        Assert.Null(_client.ReadStatus());
        var status = new UpdateStatus("1.1.15", "1.1.16", UpdatePhase.Ready, null, null, null, null, null, ["x"], null, DateTimeOffset.UtcNow);
        File.WriteAllBytes(_locations.StatusFile, UpdateStatusCodec.Serialize(status));
        Assert.Equal("1.1.16", _client.ReadStatus()!.AvailableVersion);
    }

    [Fact]
    public void Completed_update_is_announced_once_and_only_for_the_running_version()
    {
        var status = new UpdateStatus("1.1.16", null, UpdatePhase.Idle, null, null, null, null, null, [],
            new CompletedUpdate("1.1.16", DateTimeOffset.UtcNow, ["Быстрее вход"]), DateTimeOffset.UtcNow);
        Assert.Null(_client.TakeCompletedUpdateAnnouncement(status, "1.1.15"));
        Assert.Equal("1.1.16", _client.TakeCompletedUpdateAnnouncement(status, "1.1.16")!.Version);
        Assert.Null(_client.TakeCompletedUpdateAnnouncement(status, "1.1.16"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
