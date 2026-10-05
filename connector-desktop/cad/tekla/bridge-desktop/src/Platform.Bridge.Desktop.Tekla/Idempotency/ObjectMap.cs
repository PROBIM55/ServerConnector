// Persistent map externalObjectId → (teklaComponentGuid, componentType, schemaVersion).
// Хранится в %LOCALAPPDATA%\Platform\Bridge\object-map.json. Обновляется атомарно
// (write to .tmp + Move). См. plan §8.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Platform.Bridge.Desktop.Tekla.Idempotency
{
    public sealed class ObjectMap
    {
        private readonly object _lock = new();
        private readonly string _path;
        private List<ObjectMapEntry> _entries = new();

        public ObjectMap(string path)
        {
            _path = path;
            Load();
        }

        public ObjectMapEntry? FindByExternalId(string externalObjectId, string componentType)
        {
            lock (_lock)
            {
                return _entries.FirstOrDefault(e =>
                    string.Equals(e.ExternalObjectId, externalObjectId, StringComparison.Ordinal) &&
                    string.Equals(e.ComponentType, componentType, StringComparison.Ordinal));
            }
        }

        public void Upsert(ObjectMapEntry entry)
        {
            lock (_lock)
            {
                var idx = _entries.FindIndex(e =>
                    string.Equals(e.ExternalObjectId, entry.ExternalObjectId, StringComparison.Ordinal) &&
                    string.Equals(e.ComponentType, entry.ComponentType, StringComparison.Ordinal));
                if (idx >= 0) _entries[idx] = entry;
                else _entries.Add(entry);
                Persist();
            }
        }

        public void Remove(string externalObjectId, string componentType)
        {
            lock (_lock)
            {
                var removed = _entries.RemoveAll(e =>
                    string.Equals(e.ExternalObjectId, externalObjectId, StringComparison.Ordinal) &&
                    string.Equals(e.ComponentType, componentType, StringComparison.Ordinal));
                if (removed > 0) Persist();
            }
        }

        private void Load()
        {
            if (!File.Exists(_path)) return;
            try
            {
                var raw = File.ReadAllText(_path);
                if (string.IsNullOrWhiteSpace(raw)) return;
                var doc = JsonSerializer.Deserialize<ObjectMapFile>(raw, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                });
                _entries = doc?.Entries ?? new List<ObjectMapEntry>();
            }
            catch
            {
                // Corrupt file — start fresh; recovery channel is STRUCTURA_* UDA.
                _entries = new List<ObjectMapEntry>();
            }
        }

        private void Persist()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? ".");
            var tmp = _path + ".tmp";
            var json = JsonSerializer.Serialize(new ObjectMapFile { Entries = _entries },
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(tmp, json);
            // File.Move with overwrite в net48 нет; делаем delete+move
            if (File.Exists(_path)) File.Delete(_path);
            File.Move(tmp, _path);
        }

        private sealed class ObjectMapFile
        {
            public List<ObjectMapEntry> Entries { get; set; } = new();
        }
    }

    public sealed class ObjectMapEntry
    {
        public string ExternalObjectId { get; set; } = "";
        public string ComponentType { get; set; } = "";
        public int SchemaVersion { get; set; }
        public string TeklaComponentGuid { get; set; } = "";
        public int? TeklaComponentId { get; set; }
        public string? LastOperationId { get; set; }
        public DateTime LastAppliedAtUtc { get; set; }
    }
}
