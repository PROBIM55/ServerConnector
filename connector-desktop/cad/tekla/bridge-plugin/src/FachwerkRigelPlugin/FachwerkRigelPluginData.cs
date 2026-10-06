#nullable disable

using System;
using Tekla.Structures.Plugins;

namespace Structura.Tekla.Fachwerk;

public sealed class FachwerkRigelPluginData
{
    [StructuresField("fr_101_offset")]
    public double Offset101;

    [StructuresField("fr_101_d00")] public double Delta101_00 = 640;
    [StructuresField("fr_101_d01")] public double Delta101_01 = 1363;
    [StructuresField("fr_101_d02")] public double Delta101_02 = 2220;
    [StructuresField("fr_101_d03")] public double Delta101_03 = 2945;
    [StructuresField("fr_101_d04")] public double Delta101_04 = 3536;
    [StructuresField("fr_101_d05")] public double Delta101_05 = 3965;
    [StructuresField("fr_101_d06")] public double Delta101_06 = 4247;
    [StructuresField("fr_101_d07")] public double Delta101_07 = 4380;
    [StructuresField("fr_101_d08")] public double Delta101_08 = 4353;
    [StructuresField("fr_101_d09")] public double Delta101_09 = 4173;
    [StructuresField("fr_101_d10")] public double Delta101_10 = 3843;
    [StructuresField("fr_101_d11")] public double Delta101_11 = 3358;
    [StructuresField("fr_101_d12")] public double Delta101_12 = 2723;
    [StructuresField("fr_101_d13")] public double Delta101_13 = 1945;
    [StructuresField("fr_101_d14")] public double Delta101_14 = 1070;
    [StructuresField("fr_101_d15")] public double Delta101_15 = -175;

    [StructuresField("fr_101_t00")] public double Transverse101_00 = -13.275387813316653;
    [StructuresField("fr_101_t01")] public double Transverse101_01 = -28.08690631435132;
    [StructuresField("fr_101_t02")] public double Transverse101_02 = -42.89842481538599;
    [StructuresField("fr_101_t03")] public double Transverse101_03 = -44.25056389962084;
    [StructuresField("fr_101_t04")] public double Transverse101_04 = -45.60270298385569;
    [StructuresField("fr_101_t05")] public double Transverse101_05 = -46.96585894046578;
    [StructuresField("fr_101_t06")] public double Transverse101_06 = -46.63788182834096;
    [StructuresField("fr_101_t07")] public double Transverse101_07 = -45.728078409982174;
    [StructuresField("fr_101_t08")] public double Transverse101_08 = -44.8182749916234;
    [StructuresField("fr_101_t09")] public double Transverse101_09 = -44.04951388468557;
    [StructuresField("fr_101_t10")] public double Transverse101_10 = -43.78430942771342;
    [StructuresField("fr_101_t11")] public double Transverse101_11 = -43.62984098430316;
    [StructuresField("fr_101_t12")] public double Transverse101_12 = -43.475372540892906;
    [StructuresField("fr_101_t13")] public double Transverse101_13 = -39.646981983570186;
    [StructuresField("fr_101_t14")] public double Transverse101_14 = -19.823490991785093;
    [StructuresField("fr_101_t15")] public double Transverse101_15;

    [StructuresField("fr_103_offset")]
    public double Offset103;

    [StructuresField("fr_103_d00")] public double Delta103_00 = 601;
    [StructuresField("fr_103_d01")] public double Delta103_01 = 917;
    [StructuresField("fr_103_d02")] public double Delta103_02 = 1645;
    [StructuresField("fr_103_d03")] public double Delta103_03 = 2103;
    [StructuresField("fr_103_d04")] public double Delta103_04 = 2503;
    [StructuresField("fr_103_d05")] public double Delta103_05 = 2751;
    [StructuresField("fr_103_d06")] public double Delta103_06 = 2911;
    [StructuresField("fr_103_d07")] public double Delta103_07 = 2962;
    [StructuresField("fr_103_d08")] public double Delta103_08 = 2917;
    [StructuresField("fr_103_d09")] public double Delta103_09 = 2761;
    [StructuresField("fr_103_d10")] public double Delta103_10 = 2518;
    [StructuresField("fr_103_d11")] public double Delta103_11 = 2259;
    [StructuresField("fr_103_d12")] public double Delta103_12 = 1946;
    [StructuresField("fr_103_d13")] public double Delta103_13 = 1406;
    [StructuresField("fr_103_d14")] public double Delta103_14 = 807;
    [StructuresField("fr_103_d15")] public double Delta103_15 = 101;

    [StructuresField("fr_103_t00")] public double Transverse103_00 = -7.825164565526301;
    [StructuresField("fr_103_t01")] public double Transverse103_01 = -25.32904143802827;
    [StructuresField("fr_103_t02")] public double Transverse103_02 = -26.428881767760682;
    [StructuresField("fr_103_t03")] public double Transverse103_03 = -26.43144139046641;
    [StructuresField("fr_103_t04")] public double Transverse103_04 = -26.434001013172136;
    [StructuresField("fr_103_t05")] public double Transverse103_05 = -26.44004891108919;
    [StructuresField("fr_103_t06")] public double Transverse103_06 = -26.489447261064267;
    [StructuresField("fr_103_t07")] public double Transverse103_07 = -26.593493956761826;
    [StructuresField("fr_103_t08")] public double Transverse103_08 = -26.806837343904345;
    [StructuresField("fr_103_t09")] public double Transverse103_09 = -27.152282605321943;
    [StructuresField("fr_103_t10")] public double Transverse103_10 = -27.653053714847168;
    [StructuresField("fr_103_t11")] public double Transverse103_11 = -28.32049200305561;
    [StructuresField("fr_103_t12")] public double Transverse103_12 = -29.158260691774764;
    [StructuresField("fr_103_t13")] public double Transverse103_13 = -20.48826953849322;
    [StructuresField("fr_103_t14")] public double Transverse103_14 = -32.38597774768877;
    [StructuresField("fr_103_t15")] public double Transverse103_15 = -8.990978281638611;

    [StructuresField("fr_rs2_offset")]
    public double OffsetRs2;

    [StructuresField("fr_rs2_d00")] public double DeltaRs2_00 = -385;
    [StructuresField("fr_rs2_d01")] public double DeltaRs2_01 = -385;

    [StructuresField("fr_rs2_t00")] public double TransverseRs2_00 = -1.1352304150881667;
    [StructuresField("fr_rs2_t01")] public double TransverseRs2_01 = -1.1352304150881667;

    [StructuresField("fr_material")]
    public string Material = "C355-5";

    [StructuresField("fr_class")]
    public string ClassName = "20";

    public double ReadOffset(string code, double fallback)
    {
        var value = NormalizeCode(code) switch
        {
            "101" => Offset101,
            "103" => Offset103,
            "RS2" => OffsetRs2,
            _ => fallback,
        };
        return Valid(value) ? value : fallback;
    }

    public double ReadElevationDelta(string code, int ordinal, double fallback)
    {
        var values = NormalizeCode(code) switch
        {
            "101" => new[]
            {
                Delta101_00, Delta101_01, Delta101_02, Delta101_03,
                Delta101_04, Delta101_05, Delta101_06, Delta101_07,
                Delta101_08, Delta101_09, Delta101_10, Delta101_11,
                Delta101_12, Delta101_13, Delta101_14, Delta101_15,
            },
            "103" => new[]
            {
                Delta103_00, Delta103_01, Delta103_02, Delta103_03,
                Delta103_04, Delta103_05, Delta103_06, Delta103_07,
                Delta103_08, Delta103_09, Delta103_10, Delta103_11,
                Delta103_12, Delta103_13, Delta103_14, Delta103_15,
            },
            "RS2" => new[] { DeltaRs2_00, DeltaRs2_01 },
            _ => Array.Empty<double>(),
        };
        if (ordinal < 0 || ordinal >= values.Length || !Valid(values[ordinal])) return fallback;
        return values[ordinal];
    }

    public double ReadTransverseOffset(string code, int ordinal, double fallback)
    {
        var values = NormalizeCode(code) switch
        {
            "101" => new[]
            {
                Transverse101_00, Transverse101_01, Transverse101_02, Transverse101_03,
                Transverse101_04, Transverse101_05, Transverse101_06, Transverse101_07,
                Transverse101_08, Transverse101_09, Transverse101_10, Transverse101_11,
                Transverse101_12, Transverse101_13, Transverse101_14, Transverse101_15,
            },
            "103" => new[]
            {
                Transverse103_00, Transverse103_01, Transverse103_02, Transverse103_03,
                Transverse103_04, Transverse103_05, Transverse103_06, Transverse103_07,
                Transverse103_08, Transverse103_09, Transverse103_10, Transverse103_11,
                Transverse103_12, Transverse103_13, Transverse103_14, Transverse103_15,
            },
            "RS2" => new[] { TransverseRs2_00, TransverseRs2_01 },
            _ => Array.Empty<double>(),
        };
        if (ordinal < 0 || ordinal >= values.Length || !Valid(values[ordinal])) return fallback;
        return values[ordinal];
    }

    private static bool Valid(double value) =>
        !double.IsNaN(value) && !double.IsInfinity(value) &&
        value > -1_000_000 && value < 1_000_000;

    private static string NormalizeCode(string value)
    {
        var normalized = (value ?? string.Empty).Trim().ToUpperInvariant().Replace("РС", "RS");
        return normalized == "2" || normalized == "RS2" ? "RS2" : normalized;
    }
}
