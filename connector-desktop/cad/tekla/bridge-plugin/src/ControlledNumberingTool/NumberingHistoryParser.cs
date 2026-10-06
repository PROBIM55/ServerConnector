using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Structura.Tekla.ControlledNumbering
{
    public sealed class NumberingChange
    {
        public string ObjectType { get; set; }

        public Guid ObjectGuid { get; set; }

        public string Series { get; set; }

        public string OldMark { get; set; }

        public string NewMark { get; set; }

        public bool HasChanged => !string.Equals(OldMark, NewMark, StringComparison.Ordinal);

        public bool IsNew => Regex.IsMatch(OldMark ?? string.Empty, @"/0$");

        public string ObjectTypeDisplay => ObjectType == "Assembly" ? "Сборка" : "Деталь";

        public string ChangeTypeDisplay => IsNew ? "Новая марка" : "Изменена";
    }

    public sealed class NumberingSession
    {
        public string Header { get; set; }

        public bool IsComplete { get; set; }

        public bool? SynchronizeWithMasterModel { get; set; }

        public bool AutomaticCloningEnabled { get; set; }

        public bool ReuseOldNumbersEnabled { get; set; }

        public IReadOnlyList<NumberingChange> Entries { get; set; } = Array.Empty<NumberingChange>();

        public IReadOnlyList<NumberingChange> ChangedEntries =>
            Entries.Where(entry => entry.HasChanged).ToArray();
    }

    public static class NumberingHistoryParser
    {
        private const string SessionStart = "***NUMBERING_HISTORY Numbering";
        private const string SessionEnd = "***NUMBERING_HISTORY Operation finished";

        private static readonly Regex ChangeLine = new Regex(
            @"^(?<type>Part|Assembly)\s+guid:\s*(?<guid>[0-9a-fA-F-]{36})\s+series:(?<series>.*?)\s{2,}(?<old>.*?)\s+->\s+(?<new>.*?)\s*$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static NumberingSession ParseLatestCompleteSession(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new InvalidDataException("Журнал нумерации пуст.");
            }

            var endIndex = text.LastIndexOf(SessionEnd, StringComparison.Ordinal);
            if (endIndex < 0)
            {
                throw new InvalidDataException("В журнале нет завершённой сессии нумерации.");
            }

            var endOfLine = text.IndexOf('\n', endIndex);
            if (endOfLine < 0)
            {
                endOfLine = text.Length;
            }

            var startIndex = text.LastIndexOf(SessionStart, endIndex, StringComparison.Ordinal);
            if (startIndex < 0)
            {
                throw new InvalidDataException("Не найдено начало последней сессии нумерации.");
            }

            var sessionText = text.Substring(startIndex, endOfLine - startIndex);
            var entries = new List<NumberingChange>();
            string header = null;
            bool? synchronizeWithMasterModel = null;
            var automaticCloningEnabled = false;
            var reuseOldNumbersEnabled = false;

            using (var reader = new StringReader(sessionText))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (header == null && line.StartsWith(SessionStart, StringComparison.Ordinal))
                    {
                        header = line.Trim();
                    }

                    if (line.IndexOf("Synchronize with master model enabled", StringComparison.Ordinal) >= 0)
                    {
                        synchronizeWithMasterModel = true;
                    }
                    else if (line.IndexOf("Synchronize with master model disabled", StringComparison.Ordinal) >= 0)
                    {
                        synchronizeWithMasterModel = false;
                    }

                    if (line.IndexOf("Automatic cloning enabled", StringComparison.Ordinal) >= 0)
                    {
                        automaticCloningEnabled = true;
                    }

                    if (line.IndexOf("Re-use old numbers", StringComparison.Ordinal) >= 0)
                    {
                        reuseOldNumbersEnabled = true;
                    }

                    var match = ChangeLine.Match(line);
                    if (!match.Success)
                    {
                        continue;
                    }

                    if (!Guid.TryParse(match.Groups["guid"].Value, out var objectGuid))
                    {
                        continue;
                    }

                    entries.Add(new NumberingChange
                    {
                        ObjectType = match.Groups["type"].Value,
                        ObjectGuid = objectGuid,
                        Series = match.Groups["series"].Value.Trim(),
                        OldMark = match.Groups["old"].Value.Trim(),
                        NewMark = match.Groups["new"].Value.Trim()
                    });
                }
            }

            return new NumberingSession
            {
                Header = header ?? SessionStart,
                IsComplete = true,
                SynchronizeWithMasterModel = synchronizeWithMasterModel,
                AutomaticCloningEnabled = automaticCloningEnabled,
                ReuseOldNumbersEnabled = reuseOldNumbersEnabled,
                Entries = entries
            };
        }
    }
}
