// JSONL logger — каждая запись = одна строка JSON, append-only.
// Простая ротация по размеру файла. Thread-safe.

#nullable enable

using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Platform.Bridge.Desktop.Tekla.Logging
{
    public enum LogLevel { Debug, Info, Warn, Error }

    public sealed class JsonLineLogger : IDisposable
    {
        private readonly object _lock = new();
        private readonly string _path;
        private readonly long _maxBytes;
        private readonly int _maxFiles;
        private readonly bool _alsoConsole;
        private FileStream? _stream;

        public JsonLineLogger(string path, bool alsoConsole = true, long maxBytes = 10 * 1024 * 1024, int maxFiles = 5)
        {
            _path = path;
            _alsoConsole = alsoConsole;
            _maxBytes = maxBytes;
            _maxFiles = maxFiles;
            Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? ".");
            _stream = OpenStream();
        }

        public void Info(string message, object? fields = null)  => Write(LogLevel.Info, message, fields);
        public void Warn(string message, object? fields = null)  => Write(LogLevel.Warn, message, fields);
        public void Error(string message, object? fields = null) => Write(LogLevel.Error, message, fields);
        public void Debug(string message, object? fields = null) => Write(LogLevel.Debug, message, fields);

        public void Write(LogLevel level, string message, object? fields = null)
        {
            var entry = new
            {
                ts = DateTime.UtcNow.ToString("O"),
                level = level.ToString().ToLowerInvariant(),
                msg = message,
                fields,
            };
            string json;
            try { json = JsonSerializer.Serialize(entry); }
            catch (Exception ex) { json = $"{{\"ts\":\"{DateTime.UtcNow:O}\",\"level\":\"error\",\"msg\":\"log_serialize_failure\",\"err\":\"{Escape(ex.Message)}\"}}"; }

            lock (_lock)
            {
                if (_stream is null) return;
                if (_stream.Length + json.Length + 1 > _maxBytes)
                {
                    Rotate();
                }
                var bytes = Encoding.UTF8.GetBytes(json + "\n");
                _stream!.Write(bytes, 0, bytes.Length);
                _stream.Flush();
            }
            if (_alsoConsole)
            {
                Console.Out.WriteLine(json);
            }
        }

        private FileStream OpenStream()
        {
            return new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
        }

        private void Rotate()
        {
            try { _stream?.Dispose(); } catch { /* ignore */ }
            _stream = null;
            try
            {
                for (int i = _maxFiles - 1; i >= 1; i--)
                {
                    var src = $"{_path}.{i}";
                    var dst = $"{_path}.{i + 1}";
                    if (File.Exists(dst)) File.Delete(dst);
                    if (File.Exists(src)) File.Move(src, dst);
                }
                if (File.Exists(_path)) File.Move(_path, $"{_path}.1");
            }
            catch
            {
                // Best-effort rotation; if it fails we just keep appending.
            }
            _stream = OpenStream();
        }

        private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

        public void Dispose()
        {
            lock (_lock)
            {
                _stream?.Dispose();
                _stream = null;
            }
        }
    }
}
