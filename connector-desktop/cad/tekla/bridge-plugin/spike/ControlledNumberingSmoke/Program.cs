using System;
using System.IO;
using System.Linq;
using System.Text;
using Structura.Tekla.ControlledNumbering;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            ParsesLatestCompleteSessionAndCyrillicMarks();
            RejectsIncompleteSession();
            if (args.Length > 0)
            {
                ParsesRealTeklaHistory(args[0]);
            }
            Console.WriteLine("CONTROLLED_NUMBERING_SMOKE_OK");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void ParsesLatestCompleteSessionAndCyrillicMarks()
    {
        var text = string.Join("\r\n", new[]
        {
            "***NUMBERING_HISTORY Numbering (test): Thu Aug 13 10:00:00 2026",
            "NUMBERING_HISTORY Synchronize with master model enabled",
            "Part    guid: 11111111-1111-1111-1111-111111111111  series:A/1  A/1 -> A/2",
            "***NUMBERING_HISTORY Operation finished Thu Aug 13 10:00:01 2026",
            string.Empty,
            "***NUMBERING_HISTORY Numbering (test): Thu Aug 13 10:01:00 2026",
            "NUMBERING_HISTORY Synchronize with master model disabled",
            "Part    guid: 22222222-2222-2222-2222-222222222222  series:П/1  П/4 -> П/5",
            "Assembly guid: 33333333-3333-3333-3333-333333333333  series:Б/1  Б/0 -> Б/7",
            "Assembly guid: 44444444-4444-4444-4444-444444444444  series:В/1  В/8 -> В/8",
            "***NUMBERING_HISTORY Operation finished Thu Aug 13 10:01:01 2026"
        });

        var session = NumberingHistoryParser.ParseLatestCompleteSession(text);
        Assert(session.IsComplete, "Session must be complete.");
        Assert(session.SynchronizeWithMasterModel == false, "Synchronization flag was parsed incorrectly.");
        Assert(session.Entries.Count == 3, "Expected all three numbering entries.");
        Assert(session.ChangedEntries.Count == 2, "Expected only actual mark changes.");
        Assert(session.ChangedEntries.Single(change => change.ObjectType == "Part").NewMark == "П/5", "Cyrillic part mark was lost.");
        Assert(session.ChangedEntries.Single(change => change.ObjectType == "Assembly").IsNew, "New assembly mark was not detected.");
    }

    private static void RejectsIncompleteSession()
    {
        try
        {
            NumberingHistoryParser.ParseLatestCompleteSession(
                "***NUMBERING_HISTORY Numbering (test): Thu Aug 13 10:02:00 2026");
            throw new InvalidOperationException("Incomplete session must not be accepted.");
        }
        catch (System.IO.InvalidDataException)
        {
        }
    }

    private static void ParsesRealTeklaHistory(string historyPath)
    {
        var text = File.ReadAllText(historyPath, Encoding.GetEncoding(1251));
        var session = NumberingHistoryParser.ParseLatestCompleteSession(text);
        Assert(session.IsComplete, "The real Tekla session must be complete.");
        Console.WriteLine(
            $"REAL_NUMBERING_HISTORY_OK entries={session.Entries.Count} changed={session.ChangedEntries.Count}");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
