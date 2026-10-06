using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using Connector.AgrConversion;
using Connector.Desktop.Services;
using Platform.Connector.Core;

namespace Connector.UiSmoke;

internal static class EngineSmoke
{
    public static async Task<int> RunAsync(string fbxPart, string gltfpack, string ifcWorker, string outputRoot)
    {
        var output = Path.GetFullPath(outputRoot); Directory.CreateDirectory(output);
        var source = Path.Combine(output, "smoke-source.ifc");
        await File.WriteAllTextAsync(source, IfcFixture);
        await using var services = new ConnectorRuntimeServices(Path.Combine(output, "agent"), ifcWorker, gltfpack);
        var statuses = new ConcurrentQueue<ConnectorJobStatusEnvelope>();
        var executing = new HashSet<string>(); var maxExecuting = 0;
        services.Host.JobStatusChanged += status =>
        {
            statuses.Enqueue(status);
            lock (executing)
            {
                if (status.Status == JobStatus.Running) executing.Add(status.RequestId);
                if (status.Status is JobStatus.Success or JobStatus.Error or JobStatus.Cancelled or JobStatus.Timeout)
                    executing.Remove(status.RequestId);
                maxExecuting = Math.Max(maxExecuting, executing.Count);
            }
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var fbx = services.ConverterClient.SubmitAsync(Guid.NewGuid().ToString("N"), "converter.fbx-glb", fbxPart,
            Path.Combine(output, "fbx"), "default", timeout.Token);
        var ifc = services.ConverterClient.SubmitAsync(Guid.NewGuid().ToString("N"), "converter.ifc-optimize", source,
            Path.Combine(output, "ifc"), "exact", timeout.Token);
        var results = await Task.WhenAll(fbx, ifc);
        foreach (var result in results)
        {
            if (result.Status != JobStatus.Success) throw new InvalidOperationException($"{result.ExecutorId}: {result.Status} {result.ErrorCode} {result.Message}");
            var path = result.Result!.Value.GetProperty("outputPath").GetString()!;
            if (!File.Exists(path) || new FileInfo(path).Length == 0) throw new InvalidOperationException("Result file is missing.");
        }
        if (services.Host.IsRunning || services.Host.CurrentOptions is not null)
            throw new InvalidOperationException("Offline conversion started an online runtime.");
        if (maxExecuting != 1) throw new InvalidOperationException("Local execution must use one consumer.");
        using (var zip = ZipFile.OpenRead(results[0].Result!.Value.GetProperty("outputPath").GetString()!))
        {
            if (zip.GetEntry("manifest.json") is null || !zip.Entries.Any(e => e.FullName.EndsWith(".glb")))
                throw new InvalidOperationException("FBX result has no manifest/GLB.");
        }
        var optimized = await File.ReadAllTextAsync(results[1].Result!.Value.GetProperty("outputPath").GetString()!);
        if (!optimized.Contains("IFCBUILDINGELEMENTPROXY") || !optimized.Contains("SMOKE_CODE") || !optimized.Contains("retained"))
            throw new InvalidOperationException("IFC object or semantic property disappeared.");
        // Exercise the existing batch contract through exactly the same Agent, including quality report metadata.
        var legacy = await Task.Run(() => services.LegacyPartConverter.Convert(fbxPart,
            Path.Combine(output, "legacy-batch"), Path.Combine(output, "legacy-work"), new SilentProgress(), timeout.Token));
        if (!File.Exists(legacy.ZipPath) || legacy.Manifest is null || legacy.Levels.Count == 0 || string.IsNullOrWhiteSpace(legacy.PartReportJson))
            throw new InvalidOperationException("Legacy batch report/levels were not retained.");
        var drain = await services.Host.RequestDrainAsync(TimeSpan.FromSeconds(5), timeout.Token);
        if (drain.Phase != ConnectorDrainPhase.ReadyToApply || drain.ActiveOperations != 0)
            throw new InvalidOperationException("Agent did not drain completed work.");
        await File.WriteAllTextAsync(Path.Combine(output, "engine-smoke.json"), JsonSerializer.Serialize(new
        {
            outcome = "passed", offline = true, maxExecuting, drain,
            fbx = results[0], ifc = results[1],
            legacy = new { legacy.PartName, legacy.ZipBytes, levels = legacy.Levels.Count, legacy.TrianglesRef, legacy.TrianglesRefDecoded, reportRetained = true },
            statusCount = statuses.Count
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS: real FBX/Assimp/gltfpack + installed headless IFC optimizer through one offline Agent; GLB ZIP/IFC+report preserved; legacy batch metadata retained; max parallel execution=1; drain ReadyToApply.");
        return 0;
    }

    private sealed class SilentProgress : IProgress<AgrConvertProgress> { public void Report(AgrConvertProgress value) { } }
    internal const string IfcFixture = """
        ISO-10303-21;
        HEADER;
        FILE_DESCRIPTION(('Connector smoke'),'2;1');
        FILE_NAME('smoke.ifc','2026-10-01T00:00:00',('Structura'),('Structura'),'Connector smoke','Connector smoke','');
        FILE_SCHEMA(('IFC4'));
        ENDSEC;
        DATA;
        #1=IFCPROJECT('0000000000000000000001',$,'Smoke project',$,$,$,$,(#10),#12);
        #2=IFCCARTESIANPOINT((0.,0.,0.));
        #3=IFCDIRECTION((0.,0.,1.));
        #4=IFCAXIS2PLACEMENT3D(#2,#3,$);
        #5=IFCLOCALPLACEMENT($,#4);
        #10=IFCGEOMETRICREPRESENTATIONCONTEXT('Model','Model',3,1.E-5,#4,$);
        #11=IFCSIUNIT(*,.LENGTHUNIT.,$,.METRE.);
        #12=IFCUNITASSIGNMENT((#11));
        #14=IFCCARTESIANPOINT((0.,0.));
        #15=IFCAXIS2PLACEMENT2D(#14,$);
        #16=IFCRECTANGLEPROFILEDEF(.AREA.,$,#15,2.,3.);
        #17=IFCEXTRUDEDAREASOLID(#16,#4,#3,4.);
        #18=IFCSHAPEREPRESENTATION(#10,'Body','SweptSolid',(#17));
        #19=IFCPRODUCTDEFINITIONSHAPE($,$,(#18));
        #20=IFCBUILDINGELEMENTPROXY('0000000000000000000002',$,'Smoke proxy',$,$,#5,#19,$,.NOTDEFINED.);
        #21=IFCPROPERTYSINGLEVALUE('SMOKE_CODE',$,IFCLABEL('retained'),$);
        #22=IFCPROPERTYSET('0000000000000000000003',$,'Smoke properties',$,(#21));
        #23=IFCRELDEFINESBYPROPERTIES('0000000000000000000004',$,$,$,(#20),#22);
        ENDSEC;
        END-ISO-10303-21;
        """;
}
