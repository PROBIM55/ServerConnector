#nullable disable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Tekla.Structures.Model;
using Tekla.Structures.Plugins;
using TeklaBridge;

namespace Bridge.TeklaPlugin;

[Plugin("BridgeMvpPlugin")]
public class BridgeMvpPlugin : PluginBase
{
    private static readonly string TracePath = Path.Combine(Path.GetTempPath(), "bridge_mvp_plugin_trace.txt");

    private readonly BridgeMvpPluginData _data;

    public BridgeMvpPlugin(BridgeMvpPluginData data)
    {
        _data = data ?? new BridgeMvpPluginData();
        try { File.AppendAllText(TracePath, $"{DateTime.Now:HH:mm:ss.fff} CTOR{Environment.NewLine}"); } catch { }
    }

    public override List<InputDefinition> DefineInput() => new List<InputDefinition>();

    public override bool Run(List<InputDefinition> input)
    {
        _ = input;
        try
        {
            File.AppendAllText(TracePath, $"{DateTime.Now:HH:mm:ss.fff} RUN_START{Environment.NewLine}");
            var model = new Model();
            if (!model.GetConnectionStatus()) return false;

            var parts = BuildPartsFromData();
            var resultPath = Path.Combine(Path.GetTempPath(), "bridge_mvp_plugin_result.txt");
            var topMode = (_data.TopMode ?? "TOP_FLANGE").Trim().ToUpperInvariant();
            var rc = topMode == "DECK" || topMode == "DECK_SLOPES"
                ? BridgeCommands.CreateGirderStep5(model, parts, resultPath)
                : BridgeCommands.CreateGirderStep4(model, parts, resultPath);
            File.AppendAllText(TracePath, $"{DateTime.Now:HH:mm:ss.fff} rc={rc}{Environment.NewLine}");
            return rc == 0;
        }
        catch (Exception ex)
        {
            try { File.AppendAllText(TracePath, $"  EX {ex.GetType().Name}: {ex.Message}{Environment.NewLine}"); } catch { }
            return false;
        }
    }

    private string[] BuildPartsFromData()
    {
        var inv = CultureInfo.InvariantCulture;
        var positional = new List<string>(12)
        {
            "create_girder_step4",
            _data.Start ?? "0,0,0",
            _data.End ?? "24000,0,0",
            _data.H.ToString(inv),
            _data.HeightMode ?? "TO_TOP",
            _data.BottomRef ?? "TOP_LOCKED",
            _data.FlangeSegs ?? string.Empty,
            _data.WebSegs ?? string.Empty,
            _data.TopMode ?? "TOP_FLANGE",
            _data.TopData ?? string.Empty,
            _data.Material ?? "S355",
            _data.BridgeName ?? "BRIDGE_COMPONENT",
        };
        if (!string.IsNullOrWhiteSpace(_data.StressZone))
            positional.Add("stressZone=" + _data.StressZone);
        if (!string.IsNullOrWhiteSpace(_data.TopRef))
            positional.Add("topRef=" + _data.TopRef);
        try
        {
            var id = Identifier;
            if (id != null && id.ID > 0)
                positional.Add("cmpid=" + id.ID.ToString(inv));
        }
        catch { }
        return positional.ToArray();
    }
}
