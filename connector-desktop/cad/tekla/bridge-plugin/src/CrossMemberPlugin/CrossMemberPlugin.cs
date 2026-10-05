#nullable disable

using System;
using System.Collections.Generic;
using System.IO;
using Tekla.Structures.Model;
using Tekla.Structures.Plugins;
using TeklaBridge.CrossMembers;

namespace Bridge.TeklaPlugin;

[Plugin("CrossMemberPlugin")]
public class CrossMemberPlugin : PluginBase
{
    private static readonly string TracePath = Path.Combine(Path.GetTempPath(), "cross_member_plugin_trace.txt");

    private readonly CrossMemberPluginData _data;

    public CrossMemberPlugin(CrossMemberPluginData data)
    {
        _data = data ?? new CrossMemberPluginData();
    }

    public override List<InputDefinition> DefineInput()
    {
        return new List<InputDefinition>();
    }

    public override bool Run(List<InputDefinition> input)
    {
        _ = input;
        try { File.AppendAllText(TracePath, Environment.NewLine + "=== RUN_START " + DateTime.Now.ToString("s") + " ==="); } catch { }

        try
        {
            var model = new Model();
            if (!model.GetConnectionStatus())
            {
                Trace("NO_MODEL");
                return false;
            }

            var payloadJson = ResolvePayloadJson();
            var resultPath = Path.Combine(Path.GetTempPath(), "cross_member_plugin_result.txt");
            var componentId = TryComponentId();
            Trace("lineId=" + (_data.LineId ?? "<null>") +
                " payloadLen=" + payloadJson.Length +
                " payloadPath=" + (_data.PayloadPath ?? "") +
                " componentId=" + componentId);

            var rc = CrossMemberCommands.CreateCrossMemberV1(model, payloadJson, resultPath, componentId);
            try
            {
                Trace("RC=" + rc);
                if (File.Exists(resultPath)) Trace("RESULT=" + File.ReadAllText(resultPath));
            }
            catch { }
            return rc == 0;
        }
        catch (Exception ex)
        {
            try
            {
                Trace("EXCEPTION " + ex.GetType().FullName + ": " + ex.Message);
                Trace("  StackTrace: " + ex.StackTrace);
            }
            catch { }
            return false;
        }
    }

    private int TryComponentId()
    {
        try
        {
            var id = Identifier;
            return id != null ? id.ID : 0;
        }
        catch
        {
            return 0;
        }
    }

    private string ResolvePayloadJson()
    {
        var payloadPath = _data.PayloadPath;
        if (!string.IsNullOrWhiteSpace(payloadPath))
        {
            try
            {
                if (File.Exists(payloadPath))
                {
                    Trace("PAYLOAD_FROM_FILE " + payloadPath);
                    return File.ReadAllText(payloadPath);
                }
                Trace("PAYLOAD_FILE_MISSING " + payloadPath);
            }
            catch (Exception ex)
            {
                Trace("PAYLOAD_FILE_ERROR " + ex.GetType().Name + ": " + ex.Message);
            }
        }
        return _data.PayloadJson ?? "";
    }

    private static void Trace(string message)
    {
        try { File.AppendAllText(TracePath, Environment.NewLine + message); } catch { }
    }
}
