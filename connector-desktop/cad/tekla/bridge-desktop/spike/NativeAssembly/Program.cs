using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Platform.Bridge.Desktop.Tekla.ComponentRuntime;
using Platform.Bridge.Desktop.Tekla.ConstructiveRuntime;
using Platform.Contracts.TeklaPlan;
using Tekla.Structures.Geometry3d;
using Tekla.Structures.Model;

namespace NativeAssembly
{
    internal static class Program
    {
        private const double CoordinateOffset = 722000.0;

        private static int Main()
        {
            var reportDirectory = Path.Combine(Path.GetTempPath(), "NativeAssembly");
            Directory.CreateDirectory(reportDirectory);
            using var report = new StreamWriter(Path.Combine(reportDirectory, "report.txt"), append: false) { AutoFlush = true };
            Model? model = null;
            TransformationPlane? previousPlane = null;
            var partGuids = new List<string>();

            void Log(string message)
            {
                var line = $"[{DateTime.UtcNow:O}] {message}";
                Console.WriteLine(line);
                report.WriteLine(line);
            }

            try
            {
                model = new Model();
                Require(model.GetConnectionStatus(), "Tekla 2025 model is not connected");
                var info = model.GetInfo();
                Log($"MODEL name={info.ModelName} path={info.ModelPath}");
                previousPlane = model.GetWorkPlaneHandler().GetCurrentTransformationPlane();
                Require(model.GetWorkPlaneHandler().SetCurrentTransformationPlane(new TransformationPlane()), "activate global work plane");

                var main = InsertBeam(model, 0, "MAIN");
                var secondaryA = InsertBeam(model, 800, "SECONDARY-A");
                var secondaryB = InsertBeam(model, 1600, "SECONDARY-B");
                Require(model.CommitChanges(), "commit independent parts");
                partGuids.AddRange(new[] { GuidOf(model, main), GuidOf(model, secondaryA), GuidOf(model, secondaryB) });

                var initialMainAssembly = ReadAssembly(model, partGuids[0]);
                var initialMainAssemblyGuid = GuidOf(model, initialMainAssembly);
                var initialAAssemblyGuid = GuidOf(model, ReadAssembly(model, partGuids[1]));
                var initialBAssemblyGuid = GuidOf(model, ReadAssembly(model, partGuids[2]));
                VerifyAssembly(model, initialMainAssembly, partGuids[0], Array.Empty<string>());
                VerifyAssembly(model, ReadAssembly(model, partGuids[1]), partGuids[1], Array.Empty<string>());
                VerifyAssembly(model, ReadAssembly(model, partGuids[2]), partGuids[2], Array.Empty<string>());
                Log($"STEP singletonAssemblies=true main={initialMainAssemblyGuid} secondaryA={initialAAssemblyGuid} secondaryB={initialBAssemblyGuid}");

                var assembly = ReadAssembly(model, partGuids[0]);
                Require(assembly.Add(ReadPart(model, partGuids[1])), "Assembly.Add secondary A");
                Require(assembly.SetMainPart(ReadPart(model, partGuids[0])), "Assembly.SetMainPart");
                assembly.Name = "STRUCTURA ASSEMBLY SPIKE";
                Require(assembly.Modify(), "Assembly.Modify after adding secondary A");
                Require(model.CommitChanges(), "commit assembly insert");
                assembly = ReadAssembly(model, partGuids[0]);
                Require(EqualGuid(GuidOf(model, assembly), initialMainAssemblyGuid), "main assembly GUID changed after Add");
                VerifyAssembly(model, assembly, partGuids[0], new[] { partGuids[1] });
                Require(EqualGuid(GuidOf(model, ReadAssembly(model, partGuids[1])), initialMainAssemblyGuid), "secondary A did not join main assembly");
                Log("STEP create=true guidPreserved=true secondaries=1");

                assembly = ReadAssembly(model, partGuids[0]);
                Require(assembly.Add(ReadPart(model, partGuids[2])), "Assembly.Add secondary B");
                Require(assembly.Modify(), "Assembly.Modify after adding secondary B");
                Require(model.CommitChanges(), "commit assembly modify");
                assembly = ReadAssembly(model, partGuids[0]);
                Require(EqualGuid(GuidOf(model, assembly), initialMainAssemblyGuid), "main assembly GUID changed after second Add");
                VerifyAssembly(model, assembly, partGuids[0], new[] { partGuids[1], partGuids[2] });
                Log("STEP modify=true guidPreserved=true secondaries=2");

                assembly = ReadAssembly(model, partGuids[0]);
                Require(assembly.Remove(ReadPart(model, partGuids[1])), "Assembly.Remove secondary A");
                Require(assembly.Modify(), "Assembly.Modify after removing secondary A");
                Require(model.CommitChanges(), "commit assembly remove");
                assembly = ReadAssembly(model, partGuids[0]);
                VerifyAssembly(model, assembly, partGuids[0], new[] { partGuids[2] });
                var detachedAAssemblyGuid = GuidOf(model, ReadAssembly(model, partGuids[1]));
                Require(!EqualGuid(detachedAAssemblyGuid, initialMainAssemblyGuid), "secondary A still belongs to main assembly after Remove");
                Log($"STEP remove=true detachedSingleton=true detachedAssemblyGuid={detachedAAssemblyGuid}");

                assembly = ReadAssembly(model, partGuids[0]);
                Require(assembly.Remove(ReadPart(model, partGuids[2])), "Assembly.Remove secondary B during rollback");
                assembly.Name = string.Empty;
                Require(assembly.Modify(), "Assembly.Modify rollback");
                Require(model.CommitChanges(), "commit assembly rollback");
                assembly = ReadAssembly(model, partGuids[0]);
                Require(EqualGuid(GuidOf(model, assembly), initialMainAssemblyGuid), "main assembly GUID changed during rollback");
                VerifyAssembly(model, assembly, partGuids[0], Array.Empty<string>());
                VerifyAssembly(model, ReadAssembly(model, partGuids[1]), partGuids[1], Array.Empty<string>());
                VerifyAssembly(model, ReadAssembly(model, partGuids[2]), partGuids[2], Array.Empty<string>());
                Log("STEP rollback=true singletonAssembliesRestored=true mainGuidPreserved=true");

                RunExecutorProbe(model, partGuids, Log);

                Log("RESULT capability=assembly supported=true directPartMembership=true modifyGuidPreserved=true removeDetachesPart=true rollback=true featureIdsAreDependencies=true executorUpsert=true manualSecondariesPreserved=true");
                return 0;
            }
            catch (Exception exception)
            {
                Log($"FATAL {exception.GetType().Name}: {exception.Message}");
                Log(exception.ToString());
                return 99;
            }
            finally
            {
                if (model is not null)
                {
                    try { model.GetWorkPlaneHandler().SetCurrentTransformationPlane(new TransformationPlane()); }
                    catch { }
                    foreach (var guid in partGuids.AsEnumerable().Reverse())
                    {
                        try { model.SelectModelObject(model.GetIdentifierByGUID(guid))?.Delete(); }
                        catch (Exception exception) { Log($"CLEANUP_WARN part={guid}: {exception.Message}"); }
                    }
                    try { model.CommitChanges(); }
                    catch (Exception exception) { Log($"CLEANUP_WARN commit: {exception.Message}"); }
                    if (previousPlane is not null)
                    {
                        try { model.GetWorkPlaneHandler().SetCurrentTransformationPlane(previousPlane); }
                        catch (Exception exception) { Log($"CLEANUP_WARN work plane: {exception.Message}"); }
                    }
                }
            }
        }

        private static void RunExecutorProbe(Model model, ICollection<string> partGuids, Action<string> log)
        {
            log("EXECUTOR step=begin");
            var createPlan = ExecutorPlan(new[] { "assembly-secondary-a" }, "ASSEMBLY EXECUTOR V1");
            var mainCommand = ElementCommand(createPlan, "assembly-main");
            var secondaryACommand = ElementCommand(createPlan, "assembly-secondary-a");
            var secondaryBCommand = ElementCommand(createPlan, "assembly-secondary-b");
            StampPart(ReadPart(model, partGuids.ElementAt(0)), TeklaPlanOwnershipIdentity.Create(createPlan, mainCommand));
            StampPart(ReadPart(model, partGuids.ElementAt(1)), TeklaPlanOwnershipIdentity.Create(createPlan, secondaryACommand));
            StampPart(ReadPart(model, partGuids.ElementAt(2)), TeklaPlanOwnershipIdentity.Create(createPlan, secondaryBCommand));
            Require(model.CommitChanges(), "executor commit part ownership");

            var assemblyCommand = createPlan.Commands.Single(command => command.Kind == "create-assembly");
            var executor = new TeklaCreateAssemblyExecutor();
            VerifyCapabilityRegistration(executor);
            var createMutation = executor.Prepare(model, createPlan, assemblyCommand, "assembly-spike-create");
            createMutation.Apply();
            Require(model.CommitChanges(), "executor commit create");
            var createReadback = createMutation.Readback();
            RequireAssemblyReadback(
                createReadback,
                "created",
                "ASSEMBLY EXECUTOR V1",
                new[] { TeklaPlanOwnershipIdentity.Create(createPlan, secondaryACommand) });

            var manual = InsertBeam(model, 2400, "MANUAL-SECONDARY");
            Require(model.CommitChanges(), "executor commit manual Part");
            var manualGuid = GuidOf(model, manual);
            partGuids.Add(manualGuid);
            var assembly = ReadAssembly(model, partGuids.ElementAt(0));
            Require(assembly.Add(ReadPart(model, manualGuid)), "executor add manual secondary");
            Require(assembly.Modify(), "executor modify after manual secondary");
            Require(model.CommitChanges(), "executor commit manual membership");

            var modifyPlan = ExecutorPlan(
                new[] { "assembly-secondary-a", "assembly-secondary-b" },
                "ASSEMBLY EXECUTOR V2");
            var modifyCommand = modifyPlan.Commands.Single(command => command.Kind == "create-assembly");
            var modifyMutation = executor.Prepare(model, modifyPlan, modifyCommand, "assembly-spike-modify");
            modifyMutation.Apply();
            Require(model.CommitChanges(), "executor commit modify");
            var modifyReadback = modifyMutation.Readback();
            RequireAssemblyReadback(
                modifyReadback,
                "modified",
                "ASSEMBLY EXECUTOR V2",
                new[]
                {
                    TeklaPlanOwnershipIdentity.Create(modifyPlan, ElementCommand(modifyPlan, "assembly-secondary-a")),
                    TeklaPlanOwnershipIdentity.Create(modifyPlan, ElementCommand(modifyPlan, "assembly-secondary-b")),
                });
            Require(EqualGuid(createReadback.TeklaGuid, modifyReadback.TeklaGuid), "executor modify replaced Assembly identity");
            VerifyAssembly(
                model,
                ReadAssembly(model, partGuids.ElementAt(0)),
                partGuids.ElementAt(0),
                new[] { partGuids.ElementAt(1), partGuids.ElementAt(2), manualGuid });
            log($"EXECUTOR step=modify guid={modifyReadback.TeklaGuid} manualSecondaryPreserved=true");

            modifyMutation.Restore();
            Require(model.CommitChanges(), "executor commit modify rollback");
            assembly = ReadAssembly(model, partGuids.ElementAt(0));
            VerifyAssembly(
                model,
                assembly,
                partGuids.ElementAt(0),
                new[] { partGuids.ElementAt(1), manualGuid });
            Require(string.Equals(assembly.Name, "ASSEMBLY EXECUTOR V1", StringComparison.Ordinal),
                "executor modify rollback did not restore Assembly name");
            log("EXECUTOR step=modify-rollback generatedMembershipRestored=true manualSecondaryPreserved=true");

            createMutation.Restore();
            Require(model.CommitChanges(), "executor commit create rollback");
            VerifyAssembly(model, ReadAssembly(model, partGuids.ElementAt(0)), partGuids.ElementAt(0), Array.Empty<string>());
            VerifyAssembly(model, ReadAssembly(model, partGuids.ElementAt(1)), partGuids.ElementAt(1), Array.Empty<string>());
            VerifyAssembly(model, ReadAssembly(model, partGuids.ElementAt(2)), partGuids.ElementAt(2), Array.Empty<string>());
            VerifyAssembly(model, ReadAssembly(model, manualGuid), manualGuid, Array.Empty<string>());
            log("EXECUTOR step=create-rollback singletonAssembliesRestored=true");
        }

        private static void VerifyCapabilityRegistration(TeklaCreateAssemblyExecutor executor)
        {
            var registry = new TeklaNativeCommandRegistry(new ITeklaNativeCommandExecutor[] { executor });
            var capabilities = new TeklaNativeCapabilityProvider("2025.0", registry);
            Require(capabilities.Runtime.Flags.TryGetValue("assembly", out var assembly) && assembly,
                "production registration does not advertise assembly capability");
            Require(capabilities.Runtime.Flags.TryGetValue("udaStamp", out var udaStamp) && udaStamp,
                "production registration does not advertise udaStamp capability");
            Require(capabilities.Executors.TryResolve("create-assembly", out var registration) &&
                    string.Equals(registration.ExecutorId, "tekla-native.create-assembly.v1", StringComparison.Ordinal),
                "production create-assembly binding is unavailable");
        }

        private static TeklaPlanDocument ExecutorPlan(IReadOnlyCollection<string> secondaryElementIds, string name)
        {
            var main = BasePartCommand("element:assembly-main:create-beam", "assembly-main");
            var secondaryA = BasePartCommand("element:assembly-secondary-a:create-beam", "assembly-secondary-a");
            var secondaryB = BasePartCommand("element:assembly-secondary-b:create-beam", "assembly-secondary-b");
            var feature = new TeklaPlanCommand
            {
                CommandId = "feature:assembly-feature:apply-edge-treatment",
                Kind = "apply-edge-treatment",
                Phase = "edge-treatments",
                Source = new TeklaPlanSourceRef
                {
                    Kind = "feature",
                    Id = "assembly-feature",
                    StableKey = "assembly-spike/feature",
                    Role = "edge-treatment",
                    SourceLayer = "generated",
                },
                DependsOn = new[] { main.CommandId },
                Ownership = new TeklaOwnershipStamp { Namespace = TeklaPlanContract.OwnershipNamespace },
            };
            var elementCommandById = new Dictionary<string, TeklaPlanCommand>(StringComparer.Ordinal)
            {
                ["assembly-main"] = main,
                ["assembly-secondary-a"] = secondaryA,
                ["assembly-secondary-b"] = secondaryB,
            };
            var dependencies = new[] { main.CommandId }
                .Concat(secondaryElementIds.Select(elementId => elementCommandById[elementId].CommandId))
                .Concat(new[] { feature.CommandId })
                .ToArray();
            var assembly = new TeklaPlanCommand
            {
                CommandId = "assembly:executor:create-assembly",
                Kind = "create-assembly",
                Phase = "assemblies",
                Source = new TeklaPlanSourceRef
                {
                    Kind = "assembly",
                    Id = "assembly-executor",
                    StableKey = "assembly-spike/assembly",
                    Role = "assembly",
                    SourceLayer = "generated",
                },
                DependsOn = dependencies,
                Ownership = new TeklaOwnershipStamp { Namespace = TeklaPlanContract.OwnershipNamespace },
                Payload = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["mainElementId"] = JsonSerializer.SerializeToElement("assembly-main"),
                    ["secondaryElementIds"] = JsonSerializer.SerializeToElement(secondaryElementIds),
                    ["featureIds"] = JsonSerializer.SerializeToElement(new[] { "assembly-feature" }),
                    ["name"] = JsonSerializer.SerializeToElement(name),
                },
            };

            return new TeklaPlanDocument
            {
                SchemaVersion = TeklaPlanContract.SchemaVersion,
                Source = new TeklaPlanSource
                {
                    Address = new ConstructiveAddress
                    {
                        ProjectId = "assembly-spike",
                        ModuleId = "bridge",
                        ModuleVariantId = "native-assembly-executor",
                        RevisionId = "live",
                    },
                    GenerationId = "live",
                    SourceHash = "assembly-spike-source",
                    ConstructiveContentHash = "assembly-spike-constructive",
                },
                Commands = new[] { main, secondaryA, secondaryB, feature, assembly },
            };
        }

        private static TeklaPlanCommand BasePartCommand(string commandId, string elementId)
            => new()
            {
                CommandId = commandId,
                Kind = "create-beam",
                Phase = "base-parts",
                Source = new TeklaPlanSourceRef
                {
                    Kind = "element",
                    Id = elementId,
                    StableKey = "assembly-spike/" + elementId,
                    Role = "part",
                    SourceLayer = "generated",
                },
                Ownership = new TeklaOwnershipStamp { Namespace = TeklaPlanContract.OwnershipNamespace },
            };

        private static TeklaPlanCommand ElementCommand(TeklaPlanDocument plan, string elementId)
            => plan.Commands.Single(command =>
                string.Equals(command.Source.Kind, "element", StringComparison.Ordinal) &&
                string.Equals(command.Source.Id, elementId, StringComparison.Ordinal));

        private static void StampPart(Part part, string externalObjectId)
        {
            Require(part.SetUserProperty(StructuraServiceUdas.ExternalObjectId, externalObjectId),
                "executor Part ownership UDA");
            Require(part.Modify(), "executor Part ownership modify");
        }

        private static void RequireAssemblyReadback(
            TeklaNativeCommandReadback readback,
            string action,
            string name,
            IReadOnlyCollection<string> secondaryExternalObjectIds)
        {
            Require(string.Equals(readback.Action, action, StringComparison.Ordinal),
                $"executor readback action expected={action} actual={readback.Action}");
            Require(string.Equals(readback.AssemblyName, name, StringComparison.Ordinal),
                $"executor readback name expected={name} actual={readback.AssemblyName}");
            Require(readback.FeatureIds.SequenceEqual(new[] { "assembly-feature" }, StringComparer.Ordinal),
                "executor readback feature dependency mismatch");
            Require(readback.SecondaryElementExternalObjectIds.SequenceEqual(secondaryExternalObjectIds, StringComparer.Ordinal),
                "executor readback secondary ownership mismatch");
            Require(!string.IsNullOrWhiteSpace(readback.MainElementTeklaGuid) &&
                    readback.SecondaryElementTeklaGuids.Length == secondaryExternalObjectIds.Count,
                "executor readback member GUIDs are incomplete");
        }

        private static Beam InsertBeam(Model model, double y, string suffix)
        {
            var beam = new Beam(new Point(CoordinateOffset, y, 0), new Point(CoordinateOffset + 2000, y, 0))
            {
                Name = "STRUCTURA ASSEMBLY SPIKE " + suffix,
                Class = "99",
            };
            beam.Profile.ProfileString = "PL20*200";
            beam.Material.MaterialString = "S355";
            Require(beam.Insert(), "Beam.Insert " + suffix);
            return beam;
        }

        private static Part ReadPart(Model model, string guid)
        {
            var part = model.SelectModelObject(model.GetIdentifierByGUID(guid)) as Part;
            if (part is null || !part.Select()) throw new InvalidOperationException("Cannot select Part " + guid + ".");
            return part;
        }

        private static Assembly ReadAssembly(Model model, string partGuid)
        {
            var assembly = ReadPart(model, partGuid).GetAssembly();
            if (assembly is null || !assembly.Select()) throw new InvalidOperationException("Cannot select Assembly for part " + partGuid + ".");
            return assembly;
        }

        private static void VerifyAssembly(Model model, Assembly assembly, string mainGuid, IReadOnlyCollection<string> secondaryGuids)
        {
            var main = assembly.GetMainPart() as Part ?? throw new InvalidOperationException("Assembly has no main Part.");
            Require(EqualGuid(GuidOf(model, main), mainGuid), $"main part expected={mainGuid} actual={GuidOf(model, main)}");
            var actual = assembly.GetSecondaries()
                .Cast<object>()
                .OfType<Part>()
                .Select(part => GuidOf(model, part))
                .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var expected = secondaryGuids.OrderBy(static value => value, StringComparer.OrdinalIgnoreCase).ToArray();
            Require(actual.SequenceEqual(expected, StringComparer.OrdinalIgnoreCase),
                $"secondary parts expected=[{string.Join(",", expected)}] actual=[{string.Join(",", actual)}]");
        }

        private static string GuidOf(Model model, ModelObject modelObject)
        {
            var guid = model.GetGUIDByIdentifier(modelObject.Identifier);
            if (string.IsNullOrWhiteSpace(guid)) throw new InvalidOperationException("Tekla object has no persistent GUID.");
            return guid;
        }

        private static bool EqualGuid(string left, string right) =>
            string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
