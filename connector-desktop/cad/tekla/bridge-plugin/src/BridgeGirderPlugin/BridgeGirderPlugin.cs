#nullable disable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Tekla.Structures.Model;
using Tekla.Structures.Plugins;
using TeklaBridge;

namespace Bridge.TeklaPlugin;

/// <summary>
/// MINIMAL plugin для диагностики. Никаких static ctor, никаких
/// AssemblyResolve handler'ов, минимум кода — только то что в decomp
/// прежнего рабочего плагина. Если эта версия работает, постепенно
/// добавим обратно diagnostic features.
/// </summary>
[Plugin("BridgeGirderPlugin")]
public class BridgeGirderPlugin : PluginBase
{
    private readonly BridgeGirderPluginData _data;

    public BridgeGirderPlugin(BridgeGirderPluginData data)
    {
        _data = data ?? new BridgeGirderPluginData();
    }

    public override List<InputDefinition> DefineInput()
    {
        return new List<InputDefinition>();
    }

    public override bool Run(List<InputDefinition> input)
    {
        _ = input;
        string tracePath = Path.Combine(Path.GetTempPath(), "bridge_girder_plugin_trace.txt");
        try { File.AppendAllText(tracePath, Environment.NewLine + "=== RUN_START " + DateTime.Now.ToString("s") + " ==="); }
        catch { }

        try
        {
            var model = new Model();
            if (!model.GetConnectionStatus())
            {
                try { File.AppendAllText(tracePath, Environment.NewLine + "NO_MODEL"); } catch { }
                return false;
            }

            var resultPath = Path.Combine(Path.GetTempPath(), "bridge_girder_plugin_result.txt");
            var topMode = (_data.TopMode ?? "TOP_FLANGE").Trim().ToUpperInvariant();
            var hasDeck = topMode == "DECK" || topMode == "DECK_SLOPES";
            var hasRibH = ParsePositive(_data.RibH) > 0.0;
            var hasRibT = ParsePositive(_data.RibT) > 0.0;
            var hasDeckRibs = hasDeck && hasRibH && hasRibT &&
                (!string.IsNullOrWhiteSpace(_data.RibStepsLeft) || !string.IsNullOrWhiteSpace(_data.RibStepsRight));
            var hasWebRibs = !string.IsNullOrWhiteSpace(_data.WebLongRibs) || !string.IsNullOrWhiteSpace(_data.WebTransRibs);
            var useStep5 = hasDeckRibs || hasWebRibs;

            var parts = useStep5 ? BuildStep5Args() : BuildStep4Args();

            try
            {
                File.AppendAllText(tracePath, Environment.NewLine + "DISPATCH topMode=" + topMode + " step=" + (useStep5 ? "5" : "4") + " parts.Length=" + parts.Length);
                // ДАМП RAW _data ДО формирования args — показывает что fields плагин
                // получил через [StructuresField] reflection. Если значение пустое
                // здесь, значит UDA НЕ записан в Tekla / не считан плагином.
                File.AppendAllText(tracePath, Environment.NewLine + "RAW _data:");
                File.AppendAllText(tracePath, Environment.NewLine + "  WallTiltDeg='" + (_data.WallTiltDeg ?? "<null>") + "'");
                File.AppendAllText(tracePath, Environment.NewLine + "  WallTiltDegLeft='" + (_data.WallTiltDegLeft ?? "<null>") + "'");
                File.AppendAllText(tracePath, Environment.NewLine + "  WallTiltDegRight='" + (_data.WallTiltDegRight ?? "<null>") + "'");
                File.AppendAllText(tracePath, Environment.NewLine + "  BottomFlangeOrientation='" + (_data.BottomFlangeOrientation ?? "<null>") + "'");
                File.AppendAllText(tracePath, Environment.NewLine + "  TopFlangeOrientation='" + (_data.TopFlangeOrientation ?? "<null>") + "'");
                File.AppendAllText(tracePath, Environment.NewLine + "  Box='" + (_data.Box ?? "<null>") + "'");
                File.AppendAllText(tracePath, Environment.NewLine + "  TopOverhang='" + (_data.TopOverhang ?? "<null>") + "'");
                // Дамп всех аргументов для диагностики.
                for (var iArg = 0; iArg < parts.Length; iArg++)
                {
                    var a = parts[iArg] ?? "";
                    if (a.Length > 80) a = a.Substring(0, 77) + "...";
                    File.AppendAllText(tracePath, Environment.NewLine + "  [" + iArg + "] " + a);
                }
            }
            catch { }

            var rc = useStep5
                ? BridgeCommands.CreateGirderStep5(model, parts, resultPath)
                : BridgeCommands.CreateGirderStep4(model, parts, resultPath);

            try
            {
                File.AppendAllText(tracePath, Environment.NewLine + "RC=" + rc);
                if (File.Exists(resultPath))
                    File.AppendAllText(tracePath, Environment.NewLine + "RESULT=" + File.ReadAllText(resultPath));
            }
            catch { }
            return rc == 0;
        }
        catch (Exception ex)
        {
            try
            {
                File.AppendAllText(tracePath, Environment.NewLine + "EXCEPTION " + ex.GetType().FullName + ": " + ex.Message);
                File.AppendAllText(tracePath, Environment.NewLine + "  StackTrace: " + ex.StackTrace);
                for (var inner = ex.InnerException; inner != null; inner = inner.InnerException)
                    File.AppendAllText(tracePath, Environment.NewLine + "  Inner " + inner.GetType().FullName + ": " + inner.Message);
            }
            catch { }
            return false;
        }
    }

    /// <summary>
    /// Step4: 12 positional (включая команду) + named для редко-передаваемых.
    /// Базовая балка без ребер.
    /// </summary>
    private string[] BuildStep4Args()
    {
        var inv = CultureInfo.InvariantCulture;
        var args = new List<string>(18)
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
        AppendNamed(args, "topRef", _data.TopRef);
        AppendNamed(args, "placementMode", _data.PlacementMode);
        AppendNamed(args, "axisSourceObjectId", _data.AxisSourceObjectId);
        AppendSkewArgs(args);
        AppendNamed(args, "ribSection", _data.RibSection);
        AppendNamed(args, "stressZone", _data.StressZone);
        AppendWallTiltArgs(args);
        AppendFlangeOrientationArgs(args);
        AppendTwinArgs(args);
        AppendBoxArgs(args);
        AppendComponentId(args);
        return args.ToArray();
    }

    /// <summary>
    /// Step5: 16 positional (12 базовых + ribH/ribT/ribStepsLeft/ribStepsRight) +
    /// 16+ named (ribBothSides, ribStart/Length*, webLongRibs, webTransRibs,
    /// topRef, placementMode, axisSourceObjectId, ribSection, stressZone, cmpid).
    /// Mirrors decomp BridgeGirderPlugin :342-372.
    /// </summary>
    private string[] BuildStep5Args()
    {
        var inv = CultureInfo.InvariantCulture;
        var args = new List<string>(29)
        {
            "create_girder_step5",
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
            _data.RibH ?? "0",
            _data.RibT ?? "0",
            _data.RibStepsLeft ?? string.Empty,
            _data.RibStepsRight ?? string.Empty,
        };
        AppendNamed(args, "ribBothSides", _data.RibBothSides);
        AppendNamed(args, "ribStartLeft", _data.RibStartLeft);
        AppendNamed(args, "ribLengthLeft", _data.RibLengthLeft);
        AppendNamed(args, "ribStartRight", _data.RibStartRight);
        AppendNamed(args, "ribLengthRight", _data.RibLengthRight);
        AppendNamed(args, "topRef", _data.TopRef);
        AppendNamed(args, "webLongRibs", _data.WebLongRibs);
        AppendNamed(args, "webTransRibs", _data.WebTransRibs);
        AppendNamed(args, "placementMode", _data.PlacementMode);
        AppendNamed(args, "axisSourceObjectId", _data.AxisSourceObjectId);
        AppendSkewArgs(args);
        AppendNamed(args, "ribSection", _data.RibSection);
        AppendNamed(args, "stressZone", _data.StressZone);
        AppendWallTiltArgs(args);
        AppendFlangeOrientationArgs(args);
        AppendTwinArgs(args);
        AppendBoxArgs(args);
        AppendComponentId(args);
        return args.ToArray();
    }

    /// <summary>
    /// Wall tilt args — применяются и к I-girder, и к box. Раньше передавались
    /// только внутри AppendBoxArgs (когда box=true), из-за чего I-girder
    /// никогда не получал wallTiltDeg → стенка строилась вертикальной.
    /// Per-wall tilt (Left/Right) для box при wallTiltLinked=false.
    /// </summary>
    private void AppendWallTiltArgs(List<string> args)
    {
        AppendNamed(args, "wallTiltDeg", _data.WallTiltDeg);
        AppendNamed(args, "wallTiltDegLeft", _data.WallTiltDegLeft);
        AppendNamed(args, "wallTiltDegRight", _data.WallTiltDegRight);
    }

    /// <summary>
    /// Ориентация поясов (042 §3 / П-3). HORIZONTAL / PERPENDICULAR_TO_WALL.
    /// BridgeCommands игнорирует PERPENDICULAR_TO_WALL для box.
    /// </summary>
    private void AppendFlangeOrientationArgs(List<string> args)
    {
        AppendNamed(args, "bottomFlangeOrientation", _data.BottomFlangeOrientation);
        AppendNamed(args, "topFlangeOrientation", _data.TopFlangeOrientation);
    }

    /// <summary>
    /// Косина торцов блока в плане, градусы. Передаём всегда: I/box,
    /// top-flange/deck. Это контракт доставки; фактическая подрезка
    /// реализуется в TeklaBridge отдельным геометрическим слоем.
    /// </summary>
    private void AppendSkewArgs(List<string> args)
    {
        AppendNamed(args, "startSkewDeg", _data.StartSkewDeg);
        AppendNamed(args, "endSkewDeg", _data.EndSkewDeg);
    }

    /// <summary>
    /// I_TWIN_ORTHO_DECK: две независимые двутавровые. Twin=1 + twinSpacing.
    /// BridgeCommands строит две копии I-girder геометрии на ±spacing/2 в Y.
    /// </summary>
    private void AppendTwinArgs(List<string> args)
    {
        if (!IsTruthy(_data.Twin)) return;
        args.Add("twin=1");
        AppendNamed(args, "twinSpacing", _data.TwinWebSpacing);
    }

    /// <summary>
    /// Box-girder params: если box=true, передаём флаг + ширину между стенками
    /// снизу + свесы нижнего/верхнего поясов. Step4/Step5 вычисляют boxWidthTop
    /// из этих параметров + h. Per-wall ribs (webLongRibsLeft/Right +
    /// webTransRibsLeft/Right) — отдельные для каждой стенки. wallTiltDeg
    /// отдельно через AppendWallTiltArgs (применяется и к I-girder).
    /// </summary>
    private void AppendBoxArgs(List<string> args)
    {
        if (!IsTruthy(_data.Box)) return;
        args.Add("box=1");
        AppendNamed(args, "boxWidthBottom", _data.BoxWidthBottom);
        AppendNamed(args, "bottomOverhang", _data.BottomOverhang);
        AppendNamed(args, "topOverhang", _data.TopOverhang);
        if (IsTruthy(_data.BoxSplitTop)) args.Add("boxSplitTop=1");
        AppendNamed(args, "boxRotationDeg", _data.BoxRotationDeg);
        // Legacy boxWidthTop по-прежнему передаём если задан явно (для обратной
        // совместимости со стилем, который мы убираем; новый flow вычисляет
        // boxWidthTop из tilt + h в BridgeCommands).
        AppendNamed(args, "boxWidthTop", _data.BoxWidthTop);
        AppendNamed(args, "webLongRibsLeft", _data.WebLongRibsLeft);
        AppendNamed(args, "webLongRibsRight", _data.WebLongRibsRight);
        AppendNamed(args, "webTransRibsLeft", _data.WebTransRibsLeft);
        AppendNamed(args, "webTransRibsRight", _data.WebTransRibsRight);
    }

    private static bool IsTruthy(string raw)
    {
        var s = (raw ?? "").Trim().ToLowerInvariant();
        return s == "1" || s == "true" || s == "yes" || s == "y";
    }

    private static void AppendNamed(List<string> args, string key, string value)
    {
        if (!string.IsNullOrWhiteSpace(value)) args.Add(key + "=" + value);
    }

    private void AppendComponentId(List<string> args)
    {
        try
        {
            var id = Identifier;
            if (id != null && id.ID > 0)
                args.Add("cmpid=" + id.ID.ToString(CultureInfo.InvariantCulture));
        }
        catch { }
    }

    private static double ParsePositive(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return 0.0;
        return double.TryParse(raw, System.Globalization.NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v : 0.0;
    }
}
