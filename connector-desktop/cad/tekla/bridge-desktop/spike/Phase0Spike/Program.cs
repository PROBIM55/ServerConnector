// Phase 0 spike: programmatic insert + modify of BridgeGirderPlugin via Tekla Open API.
//
// Goal: prove that Bridge.Desktop can drive BridgeGirderPlugin from outside Tekla
// using documented Open API methods only. No file-based protocol, no probe.exe,
// no macro execution. The plugin lives in Tekla and renders geometry; we just
// supply its input + UDAs + invoke insert/modify.
//
// Per docs/TEKLA_BRIDGE_DESKTOP_PLAN.md §4 this spike must answer:
//   - actual class type for insert (Component vs CustomPart vs Detail vs Seam)
//   - plugin Number / Name resolution
//   - input scheme (2 points vs identifier)
//   - SetAttribute set that the plugin reads (the tb_* fields from decompile)
//   - Modify() rebuild semantics
//   - GUID handling (always via Model.GetGUIDByIdentifier per §5.3)
//   - children readback after Insert()

using System;
using System.Globalization;
using System.IO;
using Tekla.Structures.Geometry3d;
using Tekla.Structures.Model;
using Tekla.Structures;

namespace Phase0Spike
{
    internal static class Program
    {
        private const string PluginName = "BridgeGirderPlugin";
        private const string TraceFile = "phase0-spike-trace.txt";

        private static int Main(string[] args)
        {
            var traceDir = Path.Combine(Path.GetTempPath(), "Phase0Spike");
            Directory.CreateDirectory(traceDir);
            var tracePath = Path.Combine(traceDir, TraceFile);
            using var trace = new StreamWriter(File.Open(tracePath, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                AutoFlush = true
            };

            void Log(string msg)
            {
                var line = $"[{DateTime.UtcNow:O}] {msg}";
                Console.Out.WriteLine(line);
                trace.WriteLine(line);
            }

            try
            {
                Log("Phase 0 spike starting.");
                Log($"Trace file: {tracePath}");

                // ─── Step 1: Connect to Tekla ──────────────────────────────────
                var model = new Model();
                if (!model.GetConnectionStatus())
                {
                    Log("ERROR: Tekla model is not connected. Open a model in Tekla and retry.");
                    return 10;
                }
                var modelInfo = model.GetInfo();
                Log($"Connected. Model='{modelInfo.ModelName}' path='{modelInfo.ModelPath}'");

                // ─── Step 2: Build a unique location so reruns don't collide ───
                // Place the spike girder far from origin, slightly offset per run by current ticks.
                var offset = (DateTime.Now.Ticks % 100000) * 0.01;
                var startPoint = new Point(100000.0 + offset, -50000.0, 0.0);
                var endPoint = new Point(124000.0 + offset, -50000.0, 0.0);
                Log($"Start={Format(startPoint)}  End={Format(endPoint)}");

                // ─── Step 3: Programmatic insert via Component(Name=BridgeGirderPlugin) ──
                // Component is the documented class for Plugin/Connection/Detail.
                // Plugin registration is by Name ([Plugin("...")] on PluginBase).
                // PLUGIN_OBJECT_NUMBER = -100000 (constant on BaseComponent) is the
                // documented Number value for PluginBase-derived plugins.
                var component = new Component
                {
                    Name = PluginName,
                    Number = BaseComponent.PLUGIN_OBJECT_NUMBER,
                };

                // Two-point axis input scheme — matches DefineInput() in the decompiled
                // plugin: list with InputDefinition(start), InputDefinition(end).
                var input = new ComponentInput();
                input.AddTwoInputPositions(startPoint, endPoint);
                component.SetComponentInput(input);

                // UDAs the plugin reads (from BridgeGirderPluginData [StructuresField]'s).
                // Numbers chosen as a sane minimal valid girder: 24 m span, 2.2 m height,
                // 3 segments × 8 m of bottom flange / web / top flange.
                component.SetAttribute("tb_start", Format(startPoint));
                component.SetAttribute("tb_end", Format(endPoint));
                component.SetAttribute("tb_placementMode", "MANUAL_COORDS");
                component.SetAttribute("tb_axisSourceObjectId", "0");
                component.SetAttribute("tb_h", 2200.0);
                component.SetAttribute("tb_heightMode", "TO_TOP");
                component.SetAttribute("tb_bottomRef", "TOP_LOCKED");
                component.SetAttribute("tb_topRef", "TOP_LOCKED");
                component.SetAttribute("tb_stressZone", "BOTTOM_TENSION");
                component.SetAttribute("tb_flange", "8000,1200,30|8000,1200,30|8000,1200,30");
                component.SetAttribute("tb_web", "8000,2200,16|8000,2200,16|8000,2200,16");
                component.SetAttribute("tb_topMode", "TOP_FLANGE");
                component.SetAttribute("tb_topData", "8000,900,20|8000,900,20|8000,900,20");
                component.SetAttribute("tb_mat", "S355");
                component.SetAttribute("tb_name", "PHASE0_SPIKE_GIRDER");
                component.SetAttribute("tb_ribSection", "PLATE");
                component.SetAttribute("tb_ribH", "300");
                component.SetAttribute("tb_ribT", "12");
                component.SetAttribute("tb_ribBothSides", "0");
                component.SetAttribute("tb_ribStartLeft", "0");
                component.SetAttribute("tb_ribLengthLeft", "24000");
                component.SetAttribute("tb_ribStartRight", "0");
                component.SetAttribute("tb_ribLengthRight", "24000");
                component.SetAttribute("tb_ribStepsLeft", "1500|1500|1500");
                component.SetAttribute("tb_ribStepsRight", "1500|1500|1500");
                component.SetAttribute("tb_webLongRibs", "");
                component.SetAttribute("tb_webTransRibs", "");

                // Service UDAs — recovery channel per plan §5.3.
                component.SetAttribute("STRUCTURA_EXTERNAL_OBJECT_ID", "phase0-spike-bridge");
                component.SetAttribute("STRUCTURA_COMPONENT_TYPE", "BridgeGirder");
                component.SetAttribute("STRUCTURA_SCHEMA_VERSION", "1");
                component.SetAttribute("STRUCTURA_LAST_OPERATION_ID", $"phase0-{DateTime.UtcNow:yyyyMMddHHmmss}");

                Log("Calling Component.Insert()...");
                var inserted = component.Insert();
                if (!inserted)
                {
                    Log("ERROR: Component.Insert() returned false. Inspect Tekla error log + plugin trace at %TEMP%/bridge_plugin_trace.txt.");
                    return 20;
                }

                Log("Insert returned true. Calling Model.CommitChanges()...");
                if (!model.CommitChanges())
                {
                    Log("ERROR: CommitChanges() returned false.");
                    return 21;
                }

                // ─── Step 4: Capture identifiers via the documented API ────────
                // Note: GetGUIDByIdentifier returns string in Tekla 2025 (not System.Guid).
                var componentId = component.Identifier.ID;
                var componentGuid = model.GetGUIDByIdentifier(component.Identifier);
                Log($"INSERT_OK componentId={componentId} componentGuid={componentGuid}");

                // ─── Step 5: Read children for diagnostics ─────────────────────
                var childCount = 0;
                try
                {
                    var children = component.GetChildren();
                    while (children.MoveNext())
                    {
                        var child = children.Current;
                        if (child == null) continue;
                        var childId = child.Identifier?.ID ?? 0;
                        string childGuid = "";
                        try { childGuid = model.GetGUIDByIdentifier(child.Identifier); }
                        catch (Exception ex) { Log($"  child.GetGUID failed: {ex.Message}"); }
                        Log($"  child[{childCount}] id={childId} guid={childGuid} type={child.GetType().Name}");
                        childCount++;
                    }
                }
                catch (Exception ex)
                {
                    Log($"GetChildren() iteration failed: {ex.Message}");
                }
                Log($"Children total: {childCount}");

                // ─── Step 6: Modify — change height to 2400, re-commit ────────
                Log("Looking up component again by GUID for modify...");
                var lookupIdentifier = model.GetIdentifierByGUID(componentGuid);
                var roundtrip = model.SelectModelObject(lookupIdentifier) as Component;
                if (roundtrip == null)
                {
                    Log("ERROR: GUID lookup did not return a Component. Result: " +
                        (model.SelectModelObject(lookupIdentifier)?.GetType()?.Name ?? "null"));
                    return 30;
                }
                Log($"Lookup OK. Roundtrip id={roundtrip.Identifier.ID}");

                roundtrip.SetAttribute("tb_h", 2400.0);
                roundtrip.SetAttribute("STRUCTURA_LAST_OPERATION_ID", $"phase0-modify-{DateTime.UtcNow:yyyyMMddHHmmss}");

                if (!roundtrip.Modify())
                {
                    Log("ERROR: Component.Modify() returned false.");
                    return 31;
                }
                if (!model.CommitChanges())
                {
                    Log("ERROR: CommitChanges() after Modify returned false.");
                    return 32;
                }
                Log("MODIFY_OK h=2200→2400, commit OK");

                // ─── Step 7: Verify the change took effect ─────────────────────
                var verify = model.SelectModelObject(model.GetIdentifierByGUID(componentGuid)) as Component;
                if (verify == null)
                {
                    Log("WARN: post-modify lookup returned null");
                }
                else
                {
                    double readH = 0.0;
                    verify.GetAttribute("tb_h", ref readH);
                    Log($"Verify tb_h after modify: {readH.ToString(CultureInfo.InvariantCulture)}");
                    if (Math.Abs(readH - 2400.0) > 1e-6)
                    {
                        Log("WARN: tb_h did not update to 2400 — Modify may not be enough for this plugin (might require Delete+Insert).");
                    }
                }

                Log("Phase 0 spike SUCCESS");
                return 0;
            }
            catch (Exception ex)
            {
                Log($"FATAL: {ex.GetType().Name}: {ex.Message}");
                Log(ex.ToString());
                return 99;
            }
        }

        private static string Format(Point p) =>
            string.Format(CultureInfo.InvariantCulture, "{0:0.###},{1:0.###},{2:0.###}", p.X, p.Y, p.Z);
    }
}
