// Append-only operation log. Каждая операция (insert/modify/delete/upsert)
// записывается одной строкой JSON. При повторе с тем же idempotencyKey:
//   - status=success, payload совпадает → вернуть прежний result
//   - status=success, payload отличается → 409 IDEMPOTENCY_CONFLICT
//   - status=failed → разрешить повтор (insert повторно сработает,
//     возможный дубликат отловится через STRUCTURA_* UDA scan в адаптере)
//   - status=partial → §5 plan'а: попытаться найти объект по сохранённому
//     guid → modify-flow; если не найден — пометить failed и разрешить повтор.
//
// См. plan §8.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Platform.Bridge.Desktop.Tekla.Idempotency
{
    public enum IdempotencyStatus { Success, Failed, Partial }

    public sealed class IdempotencyRecord
    {
        public string IdempotencyKey { get; set; } = "";
        public string ComponentType { get; set; } = "";
        public int SchemaVersion { get; set; }
        public string Operation { get; set; } = "";
        public string ExternalObjectId { get; set; } = "";
        public string PayloadHash { get; set; } = "";
        public IdempotencyStatus Status { get; set; }
        public string? TeklaComponentGuid { get; set; }
        public int? TeklaComponentId { get; set; }
        public string? ResponseBody { get; set; }       // serialized JSON of response для replay
        public DateTime TimestampUtc { get; set; }
    }

    public sealed class IdempotencyStore
    {
        private readonly object _lock = new();
        private readonly string _path;
        private readonly List<IdempotencyRecord> _cache = new();
        private readonly int _maxCacheEntries;

        public IdempotencyStore(string path, int maxCacheEntries = 5000)
        {
            _path = path;
            _maxCacheEntries = maxCacheEntries;
            Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? ".");
            LoadRecent();
        }

        /// <summary>
        /// Найти запись по idempotencyKey. Cache-first; если не найдено в cache —
        /// в текущей реализации не сканируем весь файл (cache загружается на
        /// старте достаточным размером). Возвращает null если записи нет.
        /// </summary>
        public IdempotencyRecord? FindByKey(string idempotencyKey)
        {
            if (string.IsNullOrEmpty(idempotencyKey)) return null;
            lock (_lock)
            {
                return _cache.FirstOrDefault(r =>
                    string.Equals(r.IdempotencyKey, idempotencyKey, StringComparison.Ordinal));
            }
        }

        /// <summary>Append новый record (status=success | failed | partial).</summary>
        public void Append(IdempotencyRecord record)
        {
            if (string.IsNullOrEmpty(record.IdempotencyKey)) return;
            record.TimestampUtc = DateTime.UtcNow;

            var json = JsonSerializer.Serialize(record);
            lock (_lock)
            {
                File.AppendAllText(_path, json + "\n");
                // cache update: replace existing entry by key (newer overrides)
                var idx = _cache.FindIndex(r =>
                    string.Equals(r.IdempotencyKey, record.IdempotencyKey, StringComparison.Ordinal));
                if (idx >= 0) _cache[idx] = record;
                else _cache.Add(record);
                if (_cache.Count > _maxCacheEntries)
                {
                    _cache.RemoveRange(0, _cache.Count - _maxCacheEntries);
                }
            }
        }

        /// <summary>Стабильный hash payload'а — для проверки 409 IDEMPOTENCY_CONFLICT.</summary>
        public static string HashPayload(string payload)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(payload));
            return Convert.ToBase64String(bytes);
        }

        private void LoadRecent()
        {
            if (!File.Exists(_path)) return;
            try
            {
                var lines = File.ReadAllLines(_path);
                var start = Math.Max(0, lines.Length - _maxCacheEntries);
                for (int i = start; i < lines.Length; i++)
                {
                    var line = lines[i];
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var rec = JsonSerializer.Deserialize<IdempotencyRecord>(line,
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        if (rec is not null) _cache.Add(rec);
                    }
                    catch
                    {
                        // skip corrupt line, continue
                    }
                }
            }
            catch
            {
                // file unreadable — start fresh
                _cache.Clear();
            }
        }
    }
}
