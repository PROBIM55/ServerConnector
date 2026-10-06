#nullable disable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using Tekla.Structures.Model;

namespace Structura.Tekla.Fachwerk;

internal sealed class FachwerkColumnAssemblyApplyResult
{
    internal FachwerkColumnAssemblyApplyResult(
        IReadOnlyList<ModelObject> createdObjects,
        int assemblyCount,
        int reusedWeldCount)
    {
        CreatedObjects = createdObjects;
        AssemblyCount = assemblyCount;
        ReusedWeldCount = reusedWeldCount;
    }

    public IReadOnlyList<ModelObject> CreatedObjects { get; }
    public int AssemblyCount { get; }
    public int ReusedWeldCount { get; }
}

internal static class FachwerkColumnAssemblyTeklaAdapter
{
    private const string ManagedWeldPrefix = "FKW/";
    private const string LegacyManagedWeldPrefix = "fachwerk/assembly/";

    public static FachwerkColumnAssemblyApplyResult Apply(
        string existingKmMark,
        IReadOnlyList<FachwerkColumnPartSpec> specs,
        IReadOnlyList<Part> parts,
        FachwerkColumnAssemblyPlan plan)
    {
        if (specs == null) throw new ArgumentNullException(nameof(specs));
        if (parts == null) throw new ArgumentNullException(nameof(parts));
        if (plan == null) throw new ArgumentNullException(nameof(plan));
        if (specs.Count != parts.Count)
            throw new ArgumentException("Количество Tekla parts не совпадает с количеством физических спецификаций.");
        if (plan.AssignedPartIndices.Count != parts.Count)
            throw new ArgumentException("План сборок не покрывает все Tekla parts.", nameof(plan));

        var created = new List<ModelObject>();
        var replacedWelds = new List<ManagedWeldSnapshot>();
        var reusedWeldCount = 0;
        try
        {
            for (int index = 0; index < parts.Count; index++)
            {
                ApplyPartAttributes(existingKmMark, specs[index], parts[index]);
            }

            foreach (var segment in plan.Segments)
            {
                Part main = RequirePart(parts, segment.MainPartIndex);
                foreach (int secondaryIndex in segment.WeldSecondaryPartIndices)
                {
                    Part secondary = RequirePart(parts, secondaryIndex);
                    string weldKey = BuildWeldKey(segment, specs[secondaryIndex], secondaryIndex);
                    BaseWeld existing = FindWeld(main, secondary, weldKey);
                    if (existing != null)
                    {
                        // Tekla 2020 does not rebuild assembly ownership when only
                        // ConnectAssemblies is modified on an existing weld. Plugin
                        // output also has to be inserted on every component run.
                        ManagedWeldSnapshot snapshot = ManagedWeldSnapshot.Capture(existing);
                        if (!existing.Delete())
                            throw new InvalidOperationException("Tekla не удалила прежнюю цеховую сварку '" + weldKey + "'.");

                        Weld replacement = CreateWeld(main, secondary, weldKey);
                        if (!replacement.Insert())
                        {
                            snapshot.Restore();
                            throw new InvalidOperationException("Tekla не пересоздала цеховую сварку '" + weldKey + "'.");
                        }
                        replacedWelds.Add(snapshot);
                        created.Add(replacement);
                        continue;
                    }

                    Weld weld = CreateWeld(main, secondary, weldKey);
                    if (!weld.Insert())
                        throw new InvalidOperationException("Tekla не создала цеховую сварку '" + weldKey + "'.");
                    created.Add(weld);
                }

                Assembly assembly = main.GetAssembly();
                if (assembly == null)
                    throw new InvalidOperationException("Tekla не вернула сборку для главной левой стенки '" + segment.SegmentId + "'.");
                if (!assembly.SetMainPart(main))
                    throw new InvalidOperationException("Tekla не назначила левую стенку главной деталью '" + segment.SegmentId + "'.");
                ApplyAssemblyAttributes(existingKmMark, specs[segment.MainPartIndex], main, assembly);
            }

            return new FachwerkColumnAssemblyApplyResult(
                new ReadOnlyCollection<ModelObject>(created),
                plan.Segments.Count,
                reusedWeldCount);
        }
        catch
        {
            for (int index = created.Count - 1; index >= 0; index--)
            {
                try { created[index].Delete(); }
                catch { }
            }
            for (int index = replacedWelds.Count - 1; index >= 0; index--)
            {
                try { replacedWelds[index].Restore(); }
                catch { }
            }
            throw;
        }
    }

    private static Weld CreateWeld(Part main, Part secondary, string weldKey)
    {
        return new Weld
        {
            MainObject = main,
            SecondaryObject = secondary,
            ShopWeld = true,
            ConnectAssemblies = false,
            ReferenceText = weldKey,
        };
    }

    private static void ApplyPartAttributes(
        string mark,
        FachwerkColumnPartSpec spec,
        Part part)
    {
        FachwerkPartRole role = FachwerkPartRoleContract.FromColumnGeometryRole(spec.Role);
        var accepted = new FachwerkAcceptedPartProperties(
            part.Profile.ProfileString,
            part.Material.MaterialString,
            part.Class);
        FachwerkNumberingSpec desired = FachwerkAttributeMapper.ForColumn(role, mark, accepted);
        NumberingSeries partNumber = part.PartNumber ?? new NumberingSeries(string.Empty, 1);
        var current = new FachwerkPartAttributeSnapshot(
            ReadIdentity(part),
            "fachwerk/part/" + Uri.EscapeDataString(mark) + "/" +
                FachwerkPartRoleContract.ToSemanticName(role),
            part.Profile.ProfileString,
            part.Material.MaterialString,
            part.Class,
            part.Name,
            partNumber.Prefix,
            partNumber.StartNumber);

        bool changed = false;
        foreach (var mutation in FachwerkPartAttributeApplyContract.Build(current, desired))
        {
            switch (mutation.Field)
            {
                case FachwerkPartAttributeField.PartName:
                    part.Name = mutation.StringValue;
                    changed = true;
                    break;
                case FachwerkPartAttributeField.PartPrefix:
                    partNumber.Prefix = mutation.StringValue;
                    changed = true;
                    break;
                case FachwerkPartAttributeField.PartStartNumber:
                    partNumber.StartNumber = mutation.IntegerValue.Value;
                    changed = true;
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        if (!changed) return;
        part.PartNumber = partNumber;
        if (!part.Modify())
            throw new InvalidOperationException("Tekla не применила атрибуты детали '" + current.SemanticId + "'.");
    }

    private static void ApplyAssemblyAttributes(
        string mark,
        FachwerkColumnPartSpec mainSpec,
        Part main,
        Assembly assembly)
    {
        NumberingSeries assemblyNumber = assembly.AssemblyNumber ?? new NumberingSeries(string.Empty, 1);
        var accepted = new FachwerkAcceptedPartProperties(
            main.Profile.ProfileString,
            main.Material.MaterialString,
            main.Class);
        FachwerkNumberingSpec desired = FachwerkAttributeMapper.ForColumn(
            FachwerkPartRoleContract.FromColumnGeometryRole(mainSpec.Role),
            mark,
            accepted);
        var current = new FachwerkAssemblyAttributeSnapshot(
            assembly.Name,
            assemblyNumber.Prefix,
            assemblyNumber.StartNumber);

        foreach (var mutation in FachwerkAssemblyAttributeApplyContract.Build(current, desired))
        {
            switch (mutation.Field)
            {
                case FachwerkAssemblyAttributeField.Name:
                    assembly.Name = mutation.StringValue;
                    break;
                case FachwerkAssemblyAttributeField.Prefix:
                    assemblyNumber.Prefix = mutation.StringValue;
                    break;
                case FachwerkAssemblyAttributeField.StartNumber:
                    assemblyNumber.StartNumber = mutation.IntegerValue.Value;
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
        assembly.AssemblyNumber = assemblyNumber;
        if (!assembly.Modify())
            throw new InvalidOperationException("Tekla не применила атрибуты сборки '" + mark + "'.");
    }

    private static BaseWeld FindWeld(Part main, Part secondary, string weldKey)
    {
        var matches = new List<BaseWeld>();
        var welds = main.GetWelds();
        while (welds.MoveNext())
        {
            var weld = welds.Current as BaseWeld;
            if (weld == null || !IsManagedWeldKey(weld.ReferenceText, weldKey))
                continue;
            if (!SameObject(weld.MainObject, main) || !SameObject(weld.SecondaryObject, secondary))
                throw new InvalidOperationException("Семантический ключ сварки уже занят другой парой деталей: '" + weldKey + "'.");
            matches.Add(weld);
        }
        if (matches.Count > 1)
            throw new InvalidOperationException("Найдены дубли цеховой сварки '" + weldKey + "'.");
        return matches.Count == 1 ? matches[0] : null;
    }

    private sealed class ManagedWeldSnapshot
    {
        private ManagedWeldSnapshot(
            Part main,
            Part secondary,
            bool shopWeld,
            bool connectAssemblies,
            string referenceText)
        {
            Main = main;
            Secondary = secondary;
            ShopWeld = shopWeld;
            ConnectAssemblies = connectAssemblies;
            ReferenceText = referenceText;
        }

        private Part Main { get; }
        private Part Secondary { get; }
        private bool ShopWeld { get; }
        private bool ConnectAssemblies { get; }
        private string ReferenceText { get; }

        internal static ManagedWeldSnapshot Capture(BaseWeld weld)
        {
            return new ManagedWeldSnapshot(
                (Part)weld.MainObject,
                (Part)weld.SecondaryObject,
                weld.ShopWeld,
                weld.ConnectAssemblies,
                weld.ReferenceText);
        }

        internal void Restore()
        {
            var weld = new Weld
            {
                MainObject = Main,
                SecondaryObject = Secondary,
                ShopWeld = ShopWeld,
                ConnectAssemblies = ConnectAssemblies,
                ReferenceText = ReferenceText,
            };
            if (!weld.Insert())
                throw new InvalidOperationException("Tekla не восстановила прежнюю цеховую сварку '" + ReferenceText + "'.");
        }
    }

    internal static string BuildWeldKey(
        FachwerkColumnAssemblySegmentPlan segment,
        FachwerkColumnPartSpec secondarySpec,
        int secondaryPartIndex)
    {
        var role = FachwerkPartRoleContract.FromColumnGeometryRole(secondarySpec.Role);
        // Tekla 2020 limits BaseWeld.ReferenceText. SegmentId contains decimal
        // elevations and can exceed that limit, so persist a compact identity.
        // The physical part index is required because the 450/380 transition
        // contains two pieces with the same semantic role in one assembly.
        return ManagedWeldPrefix +
            segment.MainPartIndex.ToString("D2", CultureInfo.InvariantCulture) + "/" +
            secondaryPartIndex.ToString("D2", CultureInfo.InvariantCulture) + "/" +
            FachwerkPartRoleContract.ToSemanticName(role);
    }

    private static bool IsManagedWeldKey(string existingKey, string expectedKey)
    {
        if (string.Equals(existingKey, expectedKey, StringComparison.Ordinal))
            return true;
        return !string.IsNullOrWhiteSpace(existingKey) &&
            existingKey.StartsWith(LegacyManagedWeldPrefix, StringComparison.Ordinal);
    }

    private static Part RequirePart(IReadOnlyList<Part> parts, int index)
    {
        if (index < 0 || index >= parts.Count || parts[index] == null)
            throw new InvalidOperationException("План сборки ссылается на отсутствующую Tekla part: index=" + index + ".");
        return parts[index];
    }

    private static bool SameObject(ModelObject first, ModelObject second)
    {
        if (first == null || second == null) return false;
        if (first.Identifier.ID != 0 && second.Identifier.ID != 0)
            return first.Identifier.ID == second.Identifier.ID;
        return first.Identifier.GUID != Guid.Empty && first.Identifier.GUID == second.Identifier.GUID;
    }

    private static string ReadIdentity(ModelObject modelObject)
    {
        Guid guid = modelObject.Identifier.GUID;
        return guid != Guid.Empty
            ? guid.ToString()
            : modelObject.Identifier.ID.ToString(CultureInfo.InvariantCulture);
    }
}
