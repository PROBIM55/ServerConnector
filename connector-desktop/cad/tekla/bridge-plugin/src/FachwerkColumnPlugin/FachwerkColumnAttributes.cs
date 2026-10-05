#nullable disable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Structura.Tekla.Fachwerk;

public enum FachwerkPartRole
{
    LeftWeb,
    RightWeb,
    InnerFlange,
    OuterFlange,
    Rigel,
}

public static class FachwerkPartRoleContract
{
    public static FachwerkPartRole FromColumnGeometryRole(string value)
    {
        switch (RequireText(value, nameof(value)).ToLowerInvariant())
        {
            // Looking along the selected local +X, positive NormalOffset is
            // the left side of the section. SectionGeometry currently emits
            // outer-web with positive NormalOffset.
            case "outer-web": return FachwerkPartRole.LeftWeb;
            case "inner-web": return FachwerkPartRole.RightWeb;
            case "inner-flange": return FachwerkPartRole.InnerFlange;
            case "outer-flange": return FachwerkPartRole.OuterFlange;
            default:
                throw new ArgumentException(
                    "Роль '" + value + "' не входит в геометрию стойки Fachwerk.",
                    nameof(value));
        }
    }

    public static string ToSemanticName(FachwerkPartRole role)
    {
        switch (role)
        {
            case FachwerkPartRole.LeftWeb: return "left-web";
            case FachwerkPartRole.RightWeb: return "right-web";
            case FachwerkPartRole.InnerFlange: return "inner-flange";
            case FachwerkPartRole.OuterFlange: return "outer-flange";
            case FachwerkPartRole.Rigel: return "rigel";
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(role), role, "Неизвестная семантическая роль детали.");
        }
    }

    internal static string RequireText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Значение не может быть пустым.", parameterName);
        }

        string trimmed = value.Trim();
        if (!string.Equals(value, trimmed, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Значение не должно содержать пробелы по краям.", parameterName);
        }
        return value;
    }
}

public sealed class FachwerkAcceptedPartProperties
{
    public FachwerkAcceptedPartProperties(
        string profile,
        string material,
        string className,
        int? partStartNumber = null,
        int? assemblyStartNumber = null)
    {
        Profile = FachwerkPartRoleContract.RequireText(profile, nameof(profile));
        Material = FachwerkPartRoleContract.RequireText(material, nameof(material));
        ClassName = FachwerkPartRoleContract.RequireText(className, nameof(className));
        PartStartNumber = ValidateStartNumber(partStartNumber, nameof(partStartNumber));
        AssemblyStartNumber = ValidateStartNumber(assemblyStartNumber, nameof(assemblyStartNumber));
    }

    public string Profile { get; }
    public string Material { get; }
    public string ClassName { get; }
    public int? PartStartNumber { get; }
    public int? AssemblyStartNumber { get; }

    private static int? ValidateStartNumber(int? value, string parameterName)
    {
        if (value.HasValue && value.Value < 1)
        {
            throw new ArgumentOutOfRangeException(
                parameterName, value, "Стартовый номер должен быть положительным.");
        }
        return value;
    }
}

public sealed class FachwerkNumberingSpec
{
    internal FachwerkNumberingSpec(
        FachwerkPartRole role,
        string existingKmMark,
        string partName,
        string partPrefix,
        string assemblyName,
        string assemblyPrefix,
        FachwerkAcceptedPartProperties acceptedProperties)
    {
        Role = role;
        ExistingKmMark = existingKmMark;
        PartName = partName;
        PartPrefix = partPrefix;
        AssemblyName = assemblyName;
        AssemblyPrefix = assemblyPrefix;
        AcceptedProperties = acceptedProperties ?? throw new ArgumentNullException(nameof(acceptedProperties));
    }

    public FachwerkPartRole Role { get; }
    public string ExistingKmMark { get; }
    public string PartName { get; }
    public string PartPrefix { get; }
    public string AssemblyName { get; }
    public string AssemblyPrefix { get; }
    public FachwerkAcceptedPartProperties AcceptedProperties { get; }
}

public static class FachwerkAttributeMapper
{
    public const string PartPrefix = "515-60.";
    public const string ColumnAssemblyName = "Стойка";
    public const string RigelAssemblyName = "Ригель";

    public static FachwerkNumberingSpec ForColumn(
        FachwerkPartRole role,
        string existingKmMark,
        FachwerkAcceptedPartProperties acceptedProperties)
    {
        if (role == FachwerkPartRole.Rigel)
        {
            throw new ArgumentException("Для ригеля используйте ForRigel.", nameof(role));
        }

        string mark = RequireUnprefixedKmMark(existingKmMark, nameof(existingKmMark));
        ValidateColumnMark(mark, nameof(existingKmMark));
        return new FachwerkNumberingSpec(
            role,
            mark,
            GetColumnPartName(role),
            PartPrefix,
            ColumnAssemblyName,
            PartPrefix + mark + "-",
            acceptedProperties);
    }

    private static string GetColumnPartName(FachwerkPartRole role)
    {
        switch (role)
        {
            case FachwerkPartRole.LeftWeb:
                return "Левая стенка";
            case FachwerkPartRole.RightWeb:
                return "Правая стенка";
            case FachwerkPartRole.InnerFlange:
                return "Внутренний пояс";
            case FachwerkPartRole.OuterFlange:
                return "Наружный пояс";
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(role),
                    role,
                    "Неизвестная роль детали стойки.");
        }
    }

    public static FachwerkNumberingSpec ForRigel(
        string existingKmMark,
        FachwerkAcceptedPartProperties acceptedProperties)
    {
        string mark = RequireUnprefixedKmMark(existingKmMark, nameof(existingKmMark));
        return new FachwerkNumberingSpec(
            FachwerkPartRole.Rigel,
            mark,
            RigelAssemblyName,
            PartPrefix,
            RigelAssemblyName,
            BuildRigelAssemblyPrefix(mark),
            acceptedProperties);
    }

    public static string BuildRigelAssemblyPrefix(string existingKmMark)
    {
        string mark = RequireUnprefixedKmMark(existingKmMark, nameof(existingKmMark));
        return PartPrefix + NormalizeRigelAssemblyMark(mark);
    }

    public static string BuildColumnAssemblyPrefix(string existingKmMark)
    {
        string mark = RequireUnprefixedKmMark(existingKmMark, nameof(existingKmMark));
        ValidateColumnMark(mark, nameof(existingKmMark));
        return PartPrefix + mark + "-";
    }

    private static string NormalizeRigelAssemblyMark(string mark)
    {
        string normalized = mark.Trim().ToUpperInvariant().Replace("РС", "RS");
        return normalized switch
        {
            "101" or "RS1-101" => "РС3-",
            "103" or "RS1-103" or "RS2" or "RS-2" => "РС2-",
            _ => mark,
        };
    }

    private static string RequireUnprefixedKmMark(string value, string parameterName)
    {
        string mark = FachwerkPartRoleContract.RequireText(value, parameterName);
        if (mark.StartsWith(PartPrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Ожидается исходная марка КМ без префикса заказа '" + PartPrefix + "'.",
                parameterName);
        }
        return mark;
    }

    private static void ValidateColumnMark(string mark, string parameterName)
    {
        if (!mark.StartsWith("СФ", StringComparison.Ordinal) || mark.Length <= 2)
        {
            throw new ArgumentException(
                "Марка стойки должна быть существующей маркой КМ вида 'СФ{N}'.",
                parameterName);
        }
        for (int index = 2; index < mark.Length; index++)
        {
            if (!char.IsDigit(mark[index]))
            {
                throw new ArgumentException(
                    "Марка стойки должна быть существующей маркой КМ вида 'СФ{N}'.",
                    parameterName);
            }
        }
    }
}

public sealed class FachwerkPartAttributeSnapshot
{
    public FachwerkPartAttributeSnapshot(
        string teklaGuid,
        string semanticId,
        string profile,
        string material,
        string className,
        string partName,
        string partPrefix,
        int? partStartNumber)
    {
        TeklaGuid = FachwerkPartRoleContract.RequireText(teklaGuid, nameof(teklaGuid));
        SemanticId = FachwerkPartRoleContract.RequireText(semanticId, nameof(semanticId));
        Profile = FachwerkPartRoleContract.RequireText(profile, nameof(profile));
        Material = FachwerkPartRoleContract.RequireText(material, nameof(material));
        ClassName = FachwerkPartRoleContract.RequireText(className, nameof(className));
        PartName = partName ?? string.Empty;
        PartPrefix = partPrefix ?? string.Empty;
        PartStartNumber = partStartNumber;
    }

    public string TeklaGuid { get; }
    public string SemanticId { get; }
    public string Profile { get; }
    public string Material { get; }
    public string ClassName { get; }
    public string PartName { get; }
    public string PartPrefix { get; }
    public int? PartStartNumber { get; }
}

public enum FachwerkPartAttributeField
{
    PartName,
    PartPrefix,
    PartStartNumber,
}

public sealed class FachwerkPartAttributeMutation
{
    internal FachwerkPartAttributeMutation(
        FachwerkPartAttributeField field,
        string stringValue,
        int? integerValue)
    {
        Field = field;
        StringValue = stringValue;
        IntegerValue = integerValue;
    }

    public FachwerkPartAttributeField Field { get; }
    public string StringValue { get; }
    public int? IntegerValue { get; }
}

public static class FachwerkPartAttributeApplyContract
{
    public static IReadOnlyList<FachwerkPartAttributeMutation> Build(
        FachwerkPartAttributeSnapshot current,
        FachwerkNumberingSpec desired)
    {
        if (current == null) throw new ArgumentNullException(nameof(current));
        if (desired == null) throw new ArgumentNullException(nameof(desired));
        RequireSame("профиль", desired.AcceptedProperties.Profile, current.Profile);
        RequireSame("материал", desired.AcceptedProperties.Material, current.Material);
        RequireSame("класс", desired.AcceptedProperties.ClassName, current.ClassName);

        var result = new List<FachwerkPartAttributeMutation>();
        AddString(result, FachwerkPartAttributeField.PartName, current.PartName, desired.PartName);
        AddString(result, FachwerkPartAttributeField.PartPrefix, current.PartPrefix, desired.PartPrefix);
        if (desired.AcceptedProperties.PartStartNumber.HasValue &&
            current.PartStartNumber != desired.AcceptedProperties.PartStartNumber)
        {
            result.Add(new FachwerkPartAttributeMutation(
                FachwerkPartAttributeField.PartStartNumber,
                null,
                desired.AcceptedProperties.PartStartNumber));
        }
        return new ReadOnlyCollection<FachwerkPartAttributeMutation>(result);
    }

    private static void AddString(
        ICollection<FachwerkPartAttributeMutation> target,
        FachwerkPartAttributeField field,
        string current,
        string desired)
    {
        if (!string.Equals(current ?? string.Empty, desired ?? string.Empty, StringComparison.Ordinal))
        {
            target.Add(new FachwerkPartAttributeMutation(field, desired ?? string.Empty, null));
        }
    }

    internal static void RequireSame(string label, string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Нельзя применить атрибуты: " + label + " существующего объекта '" +
                (actual ?? "<null>") + "' не совпадает с подтверждённым значением '" + expected + "'.");
        }
    }
}

public sealed class FachwerkAssemblyAttributeSnapshot
{
    public FachwerkAssemblyAttributeSnapshot(string name, string prefix, int? startNumber)
    {
        Name = name ?? string.Empty;
        Prefix = prefix ?? string.Empty;
        StartNumber = startNumber;
    }

    public string Name { get; }
    public string Prefix { get; }
    public int? StartNumber { get; }
}

public enum FachwerkAssemblyAttributeField
{
    Name,
    Prefix,
    StartNumber,
}

public sealed class FachwerkAssemblyAttributeMutation
{
    internal FachwerkAssemblyAttributeMutation(
        FachwerkAssemblyAttributeField field,
        string stringValue,
        int? integerValue)
    {
        Field = field;
        StringValue = stringValue;
        IntegerValue = integerValue;
    }

    public FachwerkAssemblyAttributeField Field { get; }
    public string StringValue { get; }
    public int? IntegerValue { get; }
}

public static class FachwerkAssemblyAttributeApplyContract
{
    public static IReadOnlyList<FachwerkAssemblyAttributeMutation> Build(
        FachwerkAssemblyAttributeSnapshot current,
        FachwerkNumberingSpec desired)
    {
        if (current == null) throw new ArgumentNullException(nameof(current));
        if (desired == null) throw new ArgumentNullException(nameof(desired));

        var result = new List<FachwerkAssemblyAttributeMutation>();
        if (!string.Equals(current.Name, desired.AssemblyName, StringComparison.Ordinal))
        {
            result.Add(new FachwerkAssemblyAttributeMutation(
                FachwerkAssemblyAttributeField.Name, desired.AssemblyName, null));
        }
        if (!string.Equals(current.Prefix, desired.AssemblyPrefix, StringComparison.Ordinal))
        {
            result.Add(new FachwerkAssemblyAttributeMutation(
                FachwerkAssemblyAttributeField.Prefix, desired.AssemblyPrefix, null));
        }
        if (desired.AcceptedProperties.AssemblyStartNumber.HasValue &&
            current.StartNumber != desired.AcceptedProperties.AssemblyStartNumber)
        {
            result.Add(new FachwerkAssemblyAttributeMutation(
                FachwerkAssemblyAttributeField.StartNumber,
                null,
                desired.AcceptedProperties.AssemblyStartNumber));
        }
        return new ReadOnlyCollection<FachwerkAssemblyAttributeMutation>(result);
    }
}
