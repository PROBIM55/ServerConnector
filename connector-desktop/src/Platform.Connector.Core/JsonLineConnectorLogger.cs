using System.Text.Json;

namespace Platform.Connector.Core;

/// <summary>Одна строка структурного лога — для live-подписки UI.</summary>
public sealed record ConnectorLogLine(
    DateTime TsUtc,
    string Level,
    string Message,
    string? Error = null,
    string? RequestId = null);

public sealed class JsonLineConnectorLogger : IConnectorLogger, IDisposable
{
    private readonly object _gate = new();
    private readonly string? _filePath;
    private readonly bool _writeToConsole;
    private readonly Action<ConnectorLogLine>? _sink;

    public JsonLineConnectorLogger(string? filePath, bool writeToConsole = true, Action<ConnectorLogLine>? sink = null)
    {
        _filePath = string.IsNullOrWhiteSpace(filePath) ? null : filePath;
        _writeToConsole = writeToConsole;
        _sink = sink;
        if (!string.IsNullOrWhiteSpace(_filePath))
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }
    }

    public void Info(string message, ConnectorJobEnvelope? job = null, string? requestId = null)
    {
        Write("info", message, job, requestId, null);
    }

    public void Warn(string message, ConnectorJobEnvelope? job = null, string? requestId = null, Exception? exception = null)
    {
        Write("warn", message, job, requestId, exception);
    }

    public void Error(string message, ConnectorJobEnvelope? job = null, string? requestId = null, Exception? exception = null)
    {
        Write("error", message, job, requestId, exception);
    }

    public void Dispose()
    {
    }

    private void Write(string level, string message, ConnectorJobEnvelope? job, string? requestId, Exception? exception)
    {
        var tsUtc = DateTime.UtcNow;
        var evt = new
        {
            ts_utc = tsUtc,
            level,
            message,
            request_id = requestId ?? job?.RequestId,
            correlation_id = job?.CorrelationId,
            module_id = job?.ModuleId,
            provider = job?.Provider.ToString(),
            operation = job?.Operation.ToString(),
            error = exception?.Message
        };

        var line = JsonSerializer.Serialize(evt);
        lock (_gate)
        {
            if (_writeToConsole)
            {
                Console.WriteLine(line);
            }

            if (!string.IsNullOrWhiteSpace(_filePath))
            {
                try
                {
                    File.AppendAllText(_filePath, line + Environment.NewLine);
                }
                catch
                {
                    // Лог-файл не должен ронять runtime (locked/полный диск).
                }
            }
        }

        if (_sink is not null)
        {
            try
            {
                _sink(new ConnectorLogLine(tsUtc, level, message, exception?.Message, requestId ?? job?.RequestId));
            }
            catch
            {
                // UI-подписчик не должен ронять runtime.
            }
        }
    }
}
