using System.Diagnostics;
using System.Text.Json;

namespace Connector.Upgrade.Bootstrapper;

// Installation performs no sign-in or credential read. This local journal records
// only progress and the exception that would otherwise be hidden by the form.
internal sealed class StandaloneInstallJournal : IDisposable
{
    private StreamWriter? _writer;
    public string? LogPath { get; private set; }

    public static StandaloneInstallJournal Create()
    {
        var journal = new StandaloneInstallJournal();
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Structura Unified Connector", "Installer", "Logs");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory,
                "install-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmss", System.Globalization.CultureInfo.InvariantCulture) +
                "-" + Guid.NewGuid().ToString("N") + ".log");
            journal._writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read));
            journal.LogPath = path;
            journal.Record(new { utc = DateTimeOffset.UtcNow, stage = "started" });
        }
        catch (Exception) { journal.Dispose(); }
        return journal;
    }

    public void Progress(string message)
    {
        try { Record(new { utc = DateTimeOffset.UtcNow, stage = "progress", message = message[..Math.Min(message.Length, 2048)] }); }
        catch (Exception) { Dispose(); }
    }

    public void Failure(Exception error)
    {
        try
        {
            Record(new
            {
                utc = DateTimeOffset.UtcNow, stage = "failed", type = error.GetType().FullName,
                hResult = error.HResult.ToString("X8"),
                message = error.Message[..Math.Min(error.Message.Length, 2048)],
                methods = new StackTrace(error, false).GetFrames().Take(12).Select(frame => frame.GetMethod())
                    .Where(method => method is not null).Select(method => method!.DeclaringType?.FullName + "." + method.Name).ToArray()
            });
        }
        catch (Exception) { Dispose(); }
    }

    private void Record(object entry)
    {
        if (_writer is null) return;
        try { _writer.WriteLine(JsonSerializer.Serialize(entry)); _writer.Flush(); }
        catch (Exception) { Dispose(); }
    }

    public void Dispose()
    {
        try { _writer?.Dispose(); } catch (Exception) { }
        _writer = null;
    }
}
